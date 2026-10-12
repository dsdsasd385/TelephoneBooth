using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Data;
using Microsoft.Extensions.Logging;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Video;
using VContainer;
using HuliacDev.Utils;
using ZLogger;

namespace Media
{
    /// <summary>
    /// 페이지 단위로 영상(무음 반복)과 TTS(1회)를 재생하는 공용 재생기.
    /// <para>
    /// VideoPlayer 1개와 RenderTexture 1개를 모든 페이지가 공유한다. 페이지마다 플레이어나
    /// 텍스처를 새로 만들지 않으므로 페이지를 반복해서 넘겨도 힙·VRAM 할당이 늘지 않는다.
    /// 영상 URL과 TTS AudioClip은 LoadAsync에서 한 번만 만들어 두고 재생 시에는 참조만 바꾼다.
    /// </para>
    /// <para>
    /// 이전 영상의 마지막 프레임이 보이지 않도록, 페이지를 바꿀 때 RenderTexture를 비우고
    /// 대상 RawImage를 끈 뒤 새 영상의 첫 프레임이 준비된 시점(frameReady)에 다시 켠다.
    /// TTS도 같은 시점에 시작해 영상 첫 프레임과 음성 시작을 맞춘다.
    /// </para>
    /// </summary>
    public class PageMediaPlayer : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private VideoPlayer _videoPlayer;
        [SerializeField] private AudioSource _ttsSource;
        [SerializeField] private Vector2Int _renderTextureSize = new Vector2Int(1920, 1080);
        [SerializeField] private float _prepareTimeoutSeconds = 10f;
        [SerializeField] private float _maxAvDriftSeconds = 0.1f;
        [SerializeField] private float _driftCheckIntervalSeconds = 0.5f;

        #endregion

        #region Private Fields

        private static readonly ProfilerMarker LoadTtsMarker = new ProfilerMarker("PageMediaPlayer.LoadTtsAsync");

        private RenderTexture _renderTexture;
        private string[] _videoUrls = Array.Empty<string>();
        private AudioClip[] _ttsClips = Array.Empty<AudioClip>();

        // 페이지 전환 요청 번호. 전환마다 CancellationTokenSource를 새로 만들지 않고
        // 번호 비교로 이전 요청을 무효화해 전환당 힙 할당을 없앤다.
        private int _playRequestId;
        private int _pendingTtsIndex = -1;
        // Play 요청부터 TTS가 실제로 시작(또는 TTS 없음 확정)될 때까지 true. 영상 준비 중에도
        // IsTtsPlaying이 true로 유지되어, 호출자가 TTS 종료를 기다릴 때 준비 시간을 종료로 오인하지 않는다.
        private bool _isAwaitingTts;
        private RawImage _pendingSurface;
        private RawImage _currentSurface;
        private bool _prepareFailed;
        private float _prepareDeadline;
        private Func<bool> _isPrepareSettled;
        private bool _isSyncingToTts;
        private bool _isSeeking;
        private int _driftStrikeCount;
        private float _nextDriftCheckTime;

        private ILogger<PageMediaPlayer> _logger;

        #endregion

        #region Properties

        public int PageCount => _videoUrls.Length;

        /// <summary>
        /// 현재 페이지의 TTS가 재생 중이거나 곧 시작될 예정인지 여부. 입력 막기·자동 진행 판단에 쓴다.
        /// TTS가 없는 페이지는 영상 첫 프레임이 나온 뒤 false가 된다.
        /// </summary>
        public bool IsTtsPlaying => _isAwaitingTts || (_ttsSource && _ttsSource.isPlaying);

        #endregion

        #region Dependency Injection & Unity Lifecycle

        /// <summary>
        /// VContainer 의존성 주입으로 로거를 할당함.
        /// </summary>
        [Inject]
        public void Construct(ILogger<PageMediaPlayer> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 공유 RenderTexture를 한 번 만들고 VideoPlayer를 무음 반복 재생 설정으로 고정함.
        /// </summary>
        private void Awake()
        {
            _isPrepareSettled = IsPrepareSettled;
            _renderTexture = new RenderTexture(Mathf.Max(2, _renderTextureSize.x), Mathf.Max(2, _renderTextureSize.y), 0, RenderTextureFormat.ARGB32);
            _renderTexture.name = "PageMediaPlayer_RT";
            _renderTexture.Create();
            ClearRenderTexture();

            if (_videoPlayer)
            {
                ConfigureVideoPlayer();
            }
            else
            {
                Debug.LogError("[PageMediaPlayer] VideoPlayer is not assigned.");
            }

            if (_ttsSource)
            {
                _ttsSource.playOnAwake = false;
                _ttsSource.loop = false;
            }
            else
            {
                Debug.LogError("[PageMediaPlayer] TTS AudioSource is not assigned.");
            }
        }

        /// <summary>
        /// TTS 재생 중에만 영상과 TTS의 시간 차이를 확인함.
        /// </summary>
        private void Update()
        {
            if (_isSyncingToTts)
            {
                CorrectVideoDrift();
            }
        }

        /// <summary>
        /// 이벤트 구독을 풀고 RenderTexture와 TTS 클립을 해제함.
        /// </summary>
        private void OnDestroy()
        {
            if (_videoPlayer)
            {
                _videoPlayer.errorReceived -= OnVideoError;
                _videoPlayer.frameReady -= OnFrameReady;
                _videoPlayer.seekCompleted -= OnSeekCompleted;
                _videoPlayer.Stop();
                _videoPlayer.targetTexture = null;
            }

            ReleaseClips();

            if (_renderTexture)
            {
                _renderTexture.Release();
                DestroyUtil.SafeDestroy(_renderTexture);
            }
        }

        #endregion

        #region Public Methods (Load / Play / Stop)

        /// <summary>
        /// 페이지 목록의 영상 경로를 재생용 경로로 바꿔 두고 TTS 클립을 모두 미리 로드함.
        /// </summary>
        public async UniTask LoadAsync(PageMediaEntry[] pages, CancellationToken cancellationToken)
        {
            ReleaseClips();

            int count = pages != null ? pages.Length : 0;
            _videoUrls = new string[count];
            _ttsClips = new AudioClip[count];

            for (int i = 0; i < count; i++)
            {
                _videoUrls[i] = ToStreamingAssetsPath(pages[i].video);
            }

            UniTask[] loads = new UniTask[count];
            for (int i = 0; i < count; i++)
            {
                loads[i] = LoadTtsAsync(i, pages[i].tts, cancellationToken);
            }

            await UniTask.WhenAll(loads);
        }

        /// <summary>
        /// 지정한 페이지의 영상을 대상 RawImage에 재생하고, 첫 프레임이 나오면 TTS를 시작함.
        /// </summary>
        public void Play(int pageIndex, RawImage surface)
        {
            if (pageIndex < 0 || pageIndex >= _videoUrls.Length)
            {
                if (_logger != null) _logger.ZLogWarning($"[PageMediaPlayer] Page index out of range: {pageIndex}");
                return;
            }

            _playRequestId++;
            PlayAsync(_playRequestId, pageIndex, surface, this.GetCancellationTokenOnDestroy()).Forget();
        }

        /// <summary>
        /// 영상·TTS를 멈추고 화면에 남은 프레임을 지움.
        /// </summary>
        public void Stop()
        {
            _playRequestId++;
            ResetPlayback();
        }

        #endregion

        #region Playback

        /// <summary>
        /// 이전 페이지를 정리하고 새 영상을 준비해 재생하는 전환 흐름.
        /// </summary>
        private async UniTaskVoid PlayAsync(int requestId, int pageIndex, RawImage surface, CancellationToken cancellationToken)
        {
            if (!_videoPlayer) return;

            ResetPlayback();
            AttachSurface(surface);
            _isAwaitingTts = true;

            string url = _videoUrls[pageIndex];
            if (string.IsNullOrEmpty(url))
            {
                if (_logger != null) _logger.ZLogWarning($"[PageMediaPlayer] Page {pageIndex} has no video. Playing TTS only.");
                RevealSurfaceAndStartTts(pageIndex);
                return;
            }

            _prepareFailed = false;
            _prepareDeadline = Time.realtimeSinceStartup + _prepareTimeoutSeconds;
            _videoPlayer.url = url;
            _videoPlayer.Prepare();

            try
            {
                await UniTask.WaitUntil(_isPrepareSettled, PlayerLoopTiming.Update, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 기다리는 사이 다른 페이지가 요청됐으면 그 요청이 플레이어를 이미 넘겨받았다.
            if (requestId != _playRequestId) return;

            if (!_videoPlayer.isPrepared)
            {
                if (_logger != null) _logger.ZLogError($"[PageMediaPlayer] Video prepare failed or timed out: {url}");
                RevealSurfaceAndStartTts(pageIndex);
                return;
            }

            _pendingTtsIndex = pageIndex;
            _videoPlayer.sendFrameReadyEvents = true;
            _videoPlayer.Play();
        }

        /// <summary>
        /// 준비 완료, 오류, 제한 시간 초과 중 하나가 되었는지 확인함. WaitUntil에 캐시된 대리자로 넘긴다.
        /// </summary>
        private bool IsPrepareSettled()
        {
            return !_videoPlayer || _videoPlayer.isPrepared || _prepareFailed || Time.realtimeSinceStartup > _prepareDeadline;
        }

        /// <summary>
        /// 숨겨 둔 RawImage를 보이게 하고 대기 중인 페이지의 TTS를 재생함.
        /// </summary>
        private void RevealSurfaceAndStartTts(int pageIndex)
        {
            if (_pendingSurface)
            {
                _pendingSurface.enabled = true;
                _pendingSurface = null;
            }

            _pendingTtsIndex = -1;
            _isAwaitingTts = false;
            if (pageIndex < 0 || pageIndex >= _ttsClips.Length) return;

            AudioClip clip = _ttsClips[pageIndex];
            if (!clip || !_ttsSource) return;

            _ttsSource.clip = clip;
            _ttsSource.Play();
            _isSyncingToTts = _videoPlayer && _videoPlayer.isPlaying;
            _driftStrikeCount = 0;
            _nextDriftCheckTime = Time.unscaledTime + _driftCheckIntervalSeconds;
        }

        /// <summary>
        /// 재생 중인 영상·TTS를 멈추고 RenderTexture를 비우며 현재 화면의 RawImage를 끔.
        /// </summary>
        private void ResetPlayback()
        {
            _pendingTtsIndex = -1;
            _pendingSurface = null;
            _isAwaitingTts = false;
            _isSyncingToTts = false;
            _isSeeking = false;

            if (_videoPlayer)
            {
                _videoPlayer.sendFrameReadyEvents = false;
                _videoPlayer.Stop();
            }

            if (_ttsSource) _ttsSource.Stop();

            if (_currentSurface)
            {
                _currentSurface.enabled = false;
                _currentSurface.texture = null;
                _currentSurface = null;
            }

            ClearRenderTexture();
        }

        /// <summary>
        /// 공유 RenderTexture를 대상 RawImage에 연결하되 첫 프레임 전까지는 꺼 둔다.
        /// </summary>
        private void AttachSurface(RawImage surface)
        {
            if (!surface)
            {
                if (_logger != null) _logger.ZLogWarning($"[PageMediaPlayer] Video surface is null. Video will not be visible.");
                return;
            }

            surface.texture = _renderTexture;
            surface.enabled = false;
            _currentSurface = surface;
            _pendingSurface = surface;
        }

        /// <summary>
        /// 공유 RenderTexture를 투명색으로 지워 이전 영상의 프레임이 남지 않게 함.
        /// </summary>
        private void ClearRenderTexture()
        {
            if (!_renderTexture) return;

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _renderTexture;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
        }

        #endregion

        #region A/V Sync

        /// <summary>
        /// 영상이 TTS보다 허용치 이상 앞서거나 뒤처지면 영상 위치를 TTS 위치로 옮김.
        /// <para>
        /// 앱 시작 직후처럼 메인 스레드가 멈추면 영상만 늦어지고 TTS는 계속 나가 둘이 어긋난다.
        /// 음성을 되감으면 같은 말이 반복되므로 TTS를 기준으로 두고 영상을 맞춘다.
        /// </para>
        /// </summary>
        private void CorrectVideoDrift()
        {
            if (!_ttsSource || !_ttsSource.isPlaying || !_videoPlayer || !_videoPlayer.isPlaying)
            {
                _isSyncingToTts = false;
                return;
            }

            // seek가 끝나기 전의 time 값은 일시적으로 튀므로 비교하지 않는다.
            if (_isSeeking) return;

            float now = Time.unscaledTime;
            if (now < _nextDriftCheckTime) return;
            _nextDriftCheckTime = now + _driftCheckIntervalSeconds;

            double ttsTime = _ttsSource.time;
            // TTS가 영상 한 바퀴보다 길면 영상은 이미 처음으로 돌아갔으므로 더 비교하지 않는다.
            if (ttsTime >= _videoPlayer.length)
            {
                _isSyncingToTts = false;
                return;
            }

            double drift = _videoPlayer.time - ttsTime;
            if (Math.Abs(drift) <= _maxAvDriftSeconds)
            {
                _driftStrikeCount = 0;
                return;
            }

            // VideoPlayer는 멈춤 뒤 프레임을 건너뛰며 스스로 따라잡기도 하므로,
            // 한 번 어긋난 것만으로 seek하면 따라잡기와 겹쳐 반대로 어긋난다. 두 번 연속일 때만 보정한다.
            _driftStrikeCount++;
            if (_driftStrikeCount < 2) return;

            _driftStrikeCount = 0;
            _isSeeking = true;
            _videoPlayer.time = ttsTime;
            if (_logger != null) _logger.ZLogInformation($"[PageMediaPlayer] A/V drift {drift:F3}s corrected. video -> {ttsTime:F3}s");
        }

        #endregion

        #region VideoPlayer Setup & Events

        /// <summary>
        /// 영상 소리를 끄고 RenderTexture 출력·반복 재생을 설정하며 이벤트를 한 번만 구독함.
        /// </summary>
        private void ConfigureVideoPlayer()
        {
            _videoPlayer.playOnAwake = false;
            _videoPlayer.source = VideoSource.Url;
            _videoPlayer.isLooping = true;
            _videoPlayer.waitForFirstFrame = true;
            _videoPlayer.skipOnDrop = true;
            _videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            _videoPlayer.targetTexture = _renderTexture;
            // 영상 파일에 음성이 들어 있어도 재생하지 않는다. 음성은 항상 별도 TTS 파일이다.
            _videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
            _videoPlayer.sendFrameReadyEvents = false;
            _videoPlayer.errorReceived += OnVideoError;
            _videoPlayer.frameReady += OnFrameReady;
            _videoPlayer.seekCompleted += OnSeekCompleted;
        }

        /// <summary>
        /// 새 영상의 첫 프레임이 나온 순간 화면을 켜고 TTS를 시작한 뒤 프레임 이벤트를 다시 끔.
        /// </summary>
        private void OnFrameReady(VideoPlayer source, long frameIdx)
        {
            source.sendFrameReadyEvents = false;
            RevealSurfaceAndStartTts(_pendingTtsIndex);
        }

        /// <summary>
        /// 보정 seek가 끝났음을 기록하고 다음 검사를 한 주기 뒤로 미룸.
        /// </summary>
        private void OnSeekCompleted(VideoPlayer source)
        {
            _isSeeking = false;
            _nextDriftCheckTime = Time.unscaledTime + _driftCheckIntervalSeconds;
        }

        /// <summary>
        /// 영상 오류를 기록하고 준비 대기를 끝냄.
        /// </summary>
        private void OnVideoError(VideoPlayer source, string message)
        {
            _prepareFailed = true;
            if (_logger != null) _logger.ZLogError($"[PageMediaPlayer] Video error: {source.url} / {message}");
        }

        #endregion

        #region TTS Loading

        /// <summary>
        /// 한 페이지의 TTS 파일을 읽어 AudioClip으로 캐시함. 경로가 비어 있으면 건너뜀.
        /// </summary>
        private async UniTask LoadTtsAsync(int pageIndex, string relativePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(relativePath)) return;

            string uri = new Uri(ToStreamingAssetsPath(relativePath)).AbsoluteUri;
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, GetAudioType(relativePath)))
            {
                try
                {
                    await request.SendWebRequest().ToUniTask(cancellationToken: cancellationToken);
                }
                catch (UnityWebRequestException e)
                {
                    if (_logger != null) _logger.ZLogError($"[PageMediaPlayer] Failed to load TTS: {relativePath} / {e.Error}");
                    return;
                }

                using (LoadTtsMarker.Auto())
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (!clip)
                    {
                        if (_logger != null) _logger.ZLogWarning($"[PageMediaPlayer] TTS clip is null: {relativePath}");
                        return;
                    }

                    clip.name = Path.GetFileNameWithoutExtension(relativePath);
                    _ttsClips[pageIndex] = clip;
                }
            }
        }

        /// <summary>
        /// 캐시한 TTS 클립을 모두 파괴함.
        /// </summary>
        private void ReleaseClips()
        {
            if (_ttsSource) _ttsSource.clip = null;

            for (int i = 0; i < _ttsClips.Length; i++)
            {
                DestroyUtil.SafeDestroy(_ttsClips[i]);
            }

            _ttsClips = Array.Empty<AudioClip>();
        }

        /// <summary>
        /// StreamingAssets 기준 상대 경로를 전체 경로로 바꿈. 비어 있으면 빈 문자열을 돌려줌.
        /// </summary>
        private static string ToStreamingAssetsPath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return string.Empty;
            return Path.Combine(Application.streamingAssetsPath, relativePath).Replace("\\", "/");
        }

        /// <summary>
        /// 확장자로 오디오 형식을 정함.
        /// </summary>
        private static AudioType GetAudioType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".wav" => AudioType.WAV,
                ".mp3" => AudioType.MPEG,
                ".ogg" => AudioType.OGGVORBIS,
                _ => AudioType.UNKNOWN
            };
        }

        #endregion
    }
}
