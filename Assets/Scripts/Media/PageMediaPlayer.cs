using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Data;
using Microsoft.Extensions.Logging;
using R3;
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
    /// 영상 URL과 TTS AudioClip은 LoadAsync에서 한 번만 만들어 두고 재생 시에는 참조만 바꾼다.
    /// 플레이어·텍스처는 미리 만든 슬롯을 재사용하므로 페이지를 반복해서 넘겨도 힙·VRAM 할당이 늘지 않는다.
    /// </para>
    /// <para>
    /// 페이지를 바꿀 때 새 영상을 준비하는 동안은 이전 화면이 그대로 보이고, 새 영상의 첫 프레임이 나온 시점(frameReady)에
    /// 새 RawImage를 켜고 TTS를 시작한 뒤 PageShown을 발행한다. 호출자는 PageShown을 받은 같은 프레임에 화면 루트를 교체한다.
    /// </para>
    /// <para>
    /// 더블 버퍼링(SetDoubleBuffering)
    /// - 끔: 플레이어 1개. 전환 중 이전 화면은 마지막 프레임에 멈추고, 반복은 끝에서 프레임 0으로 되돌린다(약 33–50ms 멈춤).
    /// - 켬: 플레이어 2개(Front=보이는 쪽, Back=대기). Back에 같은 영상을 첫 프레임에 멈춰 두었다가 Front가 끝나면 바로 교체해
    ///   끊김 없이 반복한다. 전환 때는 Back에 새 영상을 준비하는 동안 Front가 계속 재생된다. 두 번째 텍스처는 켤 때만 만든다.
    /// </para>
    /// </summary>
    public class PageMediaPlayer : MonoBehaviour
    {
        #region Nested Types

        /// <summary>
        /// 영상 플레이어 하나와 그 출력 텍스처, 반복 대기 상태.
        /// </summary>
        private sealed class VideoSlot
        {
            public VideoPlayer Player;
            public RenderTexture Texture;
            // 반복 교체용으로 Front와 같은 영상을 첫 프레임에 멈춰 둔 상태.
            public bool IsPrimed;
            // 첫 프레임에 멈추기 위해 재생을 시작했고 아직 첫 프레임이 오지 않은 상태.
            public bool IsPriming;
        }

        #endregion

        #region Serialized Fields

        [SerializeField] private VideoPlayer _videoPlayer;
        // 더블 버퍼링을 켤 때만 쓰는 두 번째 플레이어. 비어 있으면 더블 버퍼링을 켤 수 없다.
        [SerializeField] private VideoPlayer _secondaryVideoPlayer;
        [SerializeField] private AudioSource _ttsSource;
        [SerializeField] private Vector2Int _renderTextureSize = new Vector2Int(1920, 1080);
        [SerializeField] private float _prepareTimeoutSeconds = 10f;
        // TTS 대비 영상 위치 보정(초). 양수면 영상이 그만큼 앞선 장면을 보여 준다.
        // 오디오 출력 지연이나 원본 파일 간 시작 차이를 맞출 때 쓴다.
        [SerializeField] private float _videoOffsetSeconds = 0f;
        // 영상·TTS 차이(초)당 재생 속도 조절량. 0.5면 100ms 차이에서 속도를 5% 바꾼다.
        [SerializeField] private float _speedGainPerSecond = 0.5f;
        // 재생 속도 조절 한도(비율). 0.1이면 0.9–1.1배 사이에서만 바꾼다.
        [SerializeField] private float _maxSpeedAdjust = 0.1f;
        // 이보다 작은 차이는 조절하지 않는다(프레임 단위 흔들림 무시).
        [SerializeField] private float _speedDeadZoneSeconds = 0.01f;
        // 이보다 큰 차이가 두 번 연속 확인되면 속도 조절 대신 한 번 탐색(seek)한다.
        [SerializeField] private float _seekThresholdSeconds = 0.3f;
        [SerializeField] private float _seekCheckIntervalSeconds = 0.25f;

        #endregion

        #region Private Fields

        private static readonly ProfilerMarker LoadTtsMarker = new ProfilerMarker("PageMediaPlayer.LoadTtsAsync");

        private VideoSlot _slotA;
        private VideoSlot _slotB;
        // 화면에 보이는 슬롯과 대기 슬롯. 더블 버퍼링을 끄면 Front만 쓴다.
        private VideoSlot _front;
        private VideoSlot _back;
        // 페이지 전환 중 새 영상을 준비하는 슬롯. 끔이면 Front, 켬이면 Back.
        private VideoSlot _transitionSlot;
        private bool _isDoubleBuffering;

        private string[] _videoUrls = Array.Empty<string>();
        private AudioClip[] _ttsClips = Array.Empty<AudioClip>();
        // 페이지에 속하지 않는 피드백 TTS 등 LoadClipAsync로 읽은 클립. 파괴할 때 함께 해제한다.
        private readonly List<AudioClip> _extraClips = new List<AudioClip>();

        // 페이지 전환 요청 번호. 전환마다 CancellationTokenSource를 새로 만들지 않고
        // 번호 비교로 이전 요청을 무효화해 전환당 힙 할당을 없앤다.
        private int _playRequestId;
        private int _pendingTtsIndex = -1;
        // 보이는 페이지의 영상을 반복하는지 여부. false면 끝까지 한 번 재생한 뒤 마지막 프레임에 멈춘다.
        // 전환 중에는 이전 페이지 값을 유지하다가 새 첫 프레임이 나올 때(CompleteTransition) 바꾼다.
        private bool _isLooping = true;
        private bool _pendingIsLooping = true;
        // Play 요청부터 TTS가 실제로 시작(또는 TTS 없음 확정)될 때까지 true. 영상 준비 중에도
        // IsTtsPlaying이 true로 유지되어, 호출자가 TTS 종료를 기다릴 때 준비 시간을 종료로 오인하지 않는다.
        private bool _isAwaitingTts;
        private RawImage _pendingSurface;
        private RawImage _currentSurface;
        private bool _prepareFailed;
        private float _prepareDeadline;
        private Func<bool> _isPrepareSettled;
        // TTS가 나오는 동안 true. 이때 영상 재생 속도를 조절해 TTS 위치에 맞춘다.
        private bool _isSyncingToTts;
        // 프레임 단위 흔들림을 걸러 낸 영상−TTS 차이(초).
        private double _smoothedDrift;
        private bool _isSeeking;
        private int _seekStrikeCount;
        private float _nextSeekCheckTime;
        private readonly Subject<int> _pageShown = new Subject<int>();
        private readonly Subject<Unit> _videoLoopCompleted = new Subject<Unit>();

        private ILogger<PageMediaPlayer> _logger;

        #endregion

        #region Properties

        public int PageCount => _videoUrls.Length;

        /// <summary>
        /// 더블 버퍼링이 켜져 있는지 여부.
        /// </summary>
        public bool IsDoubleBuffering => _isDoubleBuffering;

        /// <summary>
        /// 현재 페이지의 TTS가 재생 중이거나 곧 시작될 예정인지 여부. 입력 막기·자동 진행 판단에 쓴다.
        /// TTS가 없는 페이지는 영상 첫 프레임이 나온 뒤 false가 된다.
        /// </summary>
        public bool IsTtsPlaying => _isAwaitingTts || (_ttsSource && _ttsSource.isPlaying);

        /// <summary>
        /// 요청한 페이지의 영상 첫 프레임이 화면에 나온 순간 그 페이지 번호를 발행함.
        /// 화면 루트 교체는 이 시점에 해야 전환 중 검은 틈이 생기지 않는다. 다른 페이지 요청에 밀린 요청은 발행되지 않는다.
        /// </summary>
        public Observable<int> PageShown => _pageShown;

        /// <summary>
        /// 보이는 영상이 끝까지 재생된 순간(다시 처음으로 돌아가기 직전)마다 발행함. 영상을 한 번 본 뒤 넘어가는 페이지에서 쓴다.
        /// </summary>
        public Observable<Unit> VideoLoopCompleted => _videoLoopCompleted;

        /// <summary>
        /// 보이는 영상이 재생 중인지 여부. 영상이 없거나 준비에 실패한 페이지는 false.
        /// </summary>
        public bool IsVideoPlaying => _front != null && FrontPlayer && FrontPlayer.isPlaying;

        // 화면에 보이는 플레이어. 동기 맞추기는 이 플레이어에만 적용한다.
        private VideoPlayer FrontPlayer => _front.Player;

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
        /// 슬롯을 만들고 플레이어를 무음·수동 반복 설정으로 고정함. 두 번째 텍스처는 더블 버퍼링을 켤 때 만든다.
        /// </summary>
        private void Awake()
        {
            _isPrepareSettled = IsPrepareSettled;

            if (!_videoPlayer)
            {
                Debug.LogError("[PageMediaPlayer] VideoPlayer is not assigned.");
            }

            _slotA = new VideoSlot { Player = _videoPlayer };
            _slotB = new VideoSlot { Player = _secondaryVideoPlayer };
            _front = _slotA;
            _back = _slotB;

            if (_videoPlayer)
            {
                ConfigureVideoPlayer(_videoPlayer);
                EnsureSlotTexture(_slotA, "PageMediaPlayer_RT_A");
            }

            if (_secondaryVideoPlayer)
            {
                ConfigureVideoPlayer(_secondaryVideoPlayer);
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
        /// TTS 재생 중에만 영상 재생 속도를 TTS 위치에 맞춰 조절함.
        /// </summary>
        private void Update()
        {
            if (_isSyncingToTts)
            {
                SyncVideoToTts();
            }
        }

        /// <summary>
        /// 이벤트 구독을 풀고 RenderTexture와 TTS 클립을 해제함.
        /// </summary>
        private void OnDestroy()
        {
            ReleaseSlot(_slotA);
            ReleaseSlot(_slotB);
            ReleaseClips();
            ReleaseExtraClips();
            _pageShown.Dispose();
            _videoLoopCompleted.Dispose();
        }

        #endregion

        #region Public Methods (Load / Play / Stop / Double Buffering)

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
        /// 지정한 페이지의 영상을 대상 RawImage에 재생하고, 첫 프레임이 나오면 TTS를 시작하고 PageShown을 발행함.
        /// <para>
        /// isLooping이 false면 영상을 한 번만 재생하고 마지막 프레임에 멈춘다(VideoLoopCompleted는 그때 한 번 발행).
        /// 영상을 다 본 뒤 다음 화면으로 넘어가는 페이지에서, 넘어가기 전 처음부터 다시 재생되는 장면이 보이지 않게 한다.
        /// </para>
        /// </summary>
        public void Play(int pageIndex, RawImage surface, bool isLooping = true)
        {
            if (pageIndex < 0 || pageIndex >= _videoUrls.Length)
            {
                if (_logger != null) _logger.ZLogWarning($"[PageMediaPlayer] Page index out of range: {pageIndex}");
                return;
            }

            _playRequestId++;
            _pendingIsLooping = isLooping;
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

        /// <summary>
        /// 영상 없이 이미지로만 된 페이지에 들어갈 때 호출함. 보이던 영상을 멈추고 그 페이지의 TTS만 재생한다.
        /// 영상 첫 프레임을 기다리지 않으므로 PageShown을 발행하지 않으며, 호출자가 같은 프레임에 화면 루트를 바꾼다.
        /// </summary>
        public void PlayTtsOnly(int pageIndex)
        {
            Stop();
            StartTts(pageIndex);
        }

        /// <summary>
        /// 재생 중인 TTS를 끊고 주어진 클립을 재생함. 오답·정답 같은 피드백 TTS용(작업계획서 5장: 피드백 TTS는 이전 TTS를 끊는다).
        /// 영상 동기 맞추기는 하지 않는다. 클립이 없으면 TTS만 멈춘다.
        /// </summary>
        public void PlayClip(AudioClip clip)
        {
            ReleaseVideoFromTts();
            if (!_ttsSource) return;

            _ttsSource.Stop();
            if (!clip) return;

            _ttsSource.clip = clip;
            _ttsSource.Play();
        }

        /// <summary>
        /// 페이지에 속하지 않는 TTS 파일(StreamingAssets 상대 경로)을 읽어 AudioClip으로 돌려줌.
        /// 경로가 비었거나 읽지 못하면 null. 읽은 클립은 이 재생기가 파괴될 때 함께 해제된다.
        /// </summary>
        public async UniTask<AudioClip> LoadClipAsync(string relativePath, CancellationToken cancellationToken)
        {
            AudioClip clip = await LoadAudioClipAsync(relativePath, cancellationToken);
            if (clip) _extraClips.Add(clip);
            return clip;
        }

        /// <summary>
        /// 더블 버퍼링을 켜거나 끔. 켜면 두 번째 텍스처를 만들고 반복 대기를 시작하며, 끄면 두 번째 플레이어를 멈추고 텍스처를 해제함.
        /// 재생 중에 바꿔도 되며 다음 반복이나 다음 전환부터 적용된다.
        /// </summary>
        public void SetDoubleBuffering(bool isEnabled)
        {
            if (isEnabled == _isDoubleBuffering) return;

            if (isEnabled)
            {
                if (!_secondaryVideoPlayer)
                {
                    if (_logger != null) _logger.ZLogWarning($"[PageMediaPlayer] Secondary VideoPlayer is not assigned. Double buffering stays off.");
                    return;
                }

                _isDoubleBuffering = true;
                EnsureSlotTexture(_back, _back == _slotA ? "PageMediaPlayer_RT_A" : "PageMediaPlayer_RT_B");
                PrimeBackForLoop();
            }
            else
            {
                _isDoubleBuffering = false;
                // 전환 중이면 Back이 새 영상을 준비하고 있으므로 그 전환이 끝난 뒤(CompleteTransition) 정리된다.
                if (_transitionSlot != _back) ReleaseBackSlot();
            }

            if (_logger != null) _logger.ZLogInformation($"[PageMediaPlayer] Double buffering {(isEnabled ? "on" : "off")}.");
        }

        #endregion

        #region Playback

        /// <summary>
        /// 이전 화면을 보이는 채로 두고 새 영상을 준비해 재생하는 전환 흐름.
        /// </summary>
        private async UniTaskVoid PlayAsync(int requestId, int pageIndex, RawImage surface, CancellationToken cancellationToken)
        {
            if (!FrontPlayer) return;

            BeginTransition();
            AttachSurface(surface);
            _isAwaitingTts = true;
            _pendingTtsIndex = pageIndex;

            VideoSlot target = SelectTransitionSlot();
            _transitionSlot = target;

            string url = _videoUrls[pageIndex];
            if (string.IsNullOrEmpty(url))
            {
                if (_logger != null) _logger.ZLogWarning($"[PageMediaPlayer] Page {pageIndex} has no video. Playing TTS only.");
                FailTransition(target);
                return;
            }

            _prepareFailed = false;
            _prepareDeadline = Time.realtimeSinceStartup + _prepareTimeoutSeconds;
            target.Player.playbackSpeed = 1f;
            target.Player.url = url;
            target.Player.Prepare();

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

            if (!target.Player.isPrepared)
            {
                if (_logger != null) _logger.ZLogError($"[PageMediaPlayer] Video prepare failed or timed out: {url}");
                FailTransition(target);
                return;
            }

            target.Player.sendFrameReadyEvents = true;
            target.Player.Play();
        }

        /// <summary>
        /// 새 영상을 준비할 슬롯을 고름. 끔이면 Front를 그대로 쓰고, 켬이면 Front가 계속 재생되도록 Back을 쓴다.
        /// </summary>
        private VideoSlot SelectTransitionSlot()
        {
            if (!_isDoubleBuffering) return _front;

            EnsureSlotTexture(_back, _back == _slotA ? "PageMediaPlayer_RT_A" : "PageMediaPlayer_RT_B");
            _back.IsPrimed = false;
            _back.IsPriming = false;
            return _back;
        }

        /// <summary>
        /// 영상이 없거나 준비에 실패한 전환을 영상 없이 마무리함. 이전 영상이 새 화면에 남지 않게 텍스처를 비운다.
        /// </summary>
        private void FailTransition(VideoSlot target)
        {
            if (target.Player) target.Player.Stop();
            ClearRenderTexture(target.Texture);
            CompleteTransition();
        }

        /// <summary>
        /// 준비 완료, 오류, 제한 시간 초과 중 하나가 되었는지 확인함. WaitUntil에 캐시된 대리자로 넘긴다.
        /// </summary>
        private bool IsPrepareSettled()
        {
            return _transitionSlot == null || !_transitionSlot.Player || _transitionSlot.Player.isPrepared
                || _prepareFailed || Time.realtimeSinceStartup > _prepareDeadline;
        }

        /// <summary>
        /// 새 영상의 첫 프레임이 나오면 슬롯 역할을 정리하고 화면을 넘긴 뒤, 켬이면 다음 반복을 준비함.
        /// </summary>
        private void CompleteTransition()
        {
            VideoSlot shown = _transitionSlot;
            _transitionSlot = null;
            if (shown == null) return;

            if (shown != _front)
            {
                VideoSlot previous = _front;
                _front = shown;
                _back = previous;
                previous.Player.sendFrameReadyEvents = false;
                previous.Player.playbackSpeed = 1f;
                previous.IsPrimed = false;
                previous.IsPriming = false;
            }

            _isLooping = _pendingIsLooping;
            int pageIndex = _pendingTtsIndex;
            RevealSurfaceAndStartTts(pageIndex);

            if (_isDoubleBuffering)
            {
                PrimeBackForLoop();
            }
            else
            {
                // 전환 중에 더블 버퍼링을 껐다면 이제 Back을 정리한다.
                ReleaseBackSlot();
            }
        }

        /// <summary>
        /// 새 RawImage로 화면을 넘기고 대기 중인 페이지의 TTS를 재생한 뒤 PageShown을 발행함.
        /// </summary>
        private void RevealSurfaceAndStartTts(int pageIndex)
        {
            SwapSurface();
            _pendingTtsIndex = -1;
            _isAwaitingTts = false;
            StartTts(pageIndex);
            _pageShown.OnNext(pageIndex);
        }

        /// <summary>
        /// 새 화면의 RawImage에 Front 텍스처를 연결해 켜고, 이전 화면의 RawImage를 끔.
        /// </summary>
        private void SwapSurface()
        {
            if (!_pendingSurface) return;

            if (_currentSurface && _currentSurface != _pendingSurface)
            {
                _currentSurface.enabled = false;
                _currentSurface.texture = null;
            }

            _pendingSurface.texture = _front.Texture;
            _pendingSurface.enabled = true;
            _currentSurface = _pendingSurface;
            _pendingSurface = null;
        }

        /// <summary>
        /// 페이지의 TTS 클립이 있으면 재생하고, 영상이 TTS 재생 위치를 따라가게 함.
        /// </summary>
        private void StartTts(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _ttsClips.Length) return;

            AudioClip clip = _ttsClips[pageIndex];
            if (!clip || !_ttsSource) return;

            _ttsSource.clip = clip;
            _ttsSource.Play();

            if (FrontPlayer && FrontPlayer.isPlaying)
            {
                BindVideoToTts();
            }
        }

        /// <summary>
        /// 페이지 전환을 시작함. 이전 TTS와 진행 중인 대기만 멈추고, 보이는 영상은 새 첫 프레임이 나올 때까지 그대로 둔다
        /// (지우거나 숨기면 전환 사이에 검은 틈이 생긴다). 켬이면 Front는 계속 재생된다.
        /// </summary>
        private void BeginTransition()
        {
            _pendingTtsIndex = -1;
            _pendingSurface = null;
            _isAwaitingTts = false;
            _transitionSlot = null;
            ReleaseVideoFromTts();

            if (FrontPlayer) FrontPlayer.sendFrameReadyEvents = false;
            if (_back.Player)
            {
                _back.Player.sendFrameReadyEvents = false;
                _back.IsPriming = false;
            }

            if (_ttsSource) _ttsSource.Stop();
        }

        /// <summary>
        /// 재생 중인 영상·TTS를 모두 멈추고 텍스처를 비우며 현재 화면의 RawImage를 끔.
        /// </summary>
        private void ResetPlayback()
        {
            BeginTransition();
            StopSlot(_slotA);
            StopSlot(_slotB);

            if (_currentSurface)
            {
                _currentSurface.enabled = false;
                _currentSurface.texture = null;
                _currentSurface = null;
            }
        }

        /// <summary>
        /// 새 화면의 RawImage를 기억하고 첫 프레임 전까지는 꺼 둔다. 텍스처는 교체 순간(SwapSurface)에 연결한다.
        /// 지금 보이는 RawImage와 같으면 끄지 않는다(같은 화면을 다시 띄울 때 검은 틈 방지).
        /// </summary>
        private void AttachSurface(RawImage surface)
        {
            if (!surface)
            {
                if (_logger != null) _logger.ZLogWarning($"[PageMediaPlayer] Video surface is null. Video will not be visible.");
                return;
            }

            if (surface != _currentSurface) surface.enabled = false;
            _pendingSurface = surface;
        }

        #endregion

        #region Loop & Double Buffering

        /// <summary>
        /// 영상이 끝나면 반복함. 켬이고 Back이 같은 영상을 첫 프레임에 대기 중이면 바로 교체하고, 아니면 프레임 0으로 되돌린다.
        /// VideoPlayer 자체 반복(isLooping)은 처음으로 돌아간 직후 한 프레임을 이전 프레임이 섞인 채 내보내 쓰지 않는다
        /// (당시 메인03, 현재 메인02 영상에서 밝기 12→19로 튐, 2026-10-12 측정).
        /// </summary>
        private void OnLoopPointReached(VideoPlayer source)
        {
            if (source != FrontPlayer) return;

            _videoLoopCompleted.OnNext(Unit.Default);

            // 한 번만 재생하는 페이지는 되돌리지 않는다. isLooping이 꺼져 있어 플레이어는 마지막 프레임을 보인 채 멈춘다.
            // TTS가 영상보다 길어도 더는 맞추지 않는다. 맞추면 멈춘 영상을 TTS 위치로 계속 탐색해 끊겨 보인다.
            if (!_isLooping)
            {
                ReleaseVideoFromTts();
                return;
            }

            if (_isDoubleBuffering && _back.IsPrimed && _back.Player && _back.Player.url == source.url && _currentSurface)
            {
                SwapToPrimedBack();
                return;
            }

            source.frame = 0;
            source.Play();
        }

        /// <summary>
        /// 첫 프레임에 대기 중인 Back으로 화면을 바로 넘기고 재생을 이어 간 뒤, 이전 Front를 다음 반복용으로 다시 준비함.
        /// </summary>
        private void SwapToPrimedBack()
        {
            VideoSlot previous = _front;
            _front = _back;
            _back = previous;

            _front.IsPrimed = false;
            _front.Player.playbackSpeed = previous.Player.playbackSpeed;
            _currentSurface.texture = _front.Texture;
            _front.Player.Play();

            previous.Player.playbackSpeed = 1f;
            PrimeBackForLoop();
        }

        /// <summary>
        /// Back에 Front와 같은 영상을 첫 프레임까지 재생해 멈춰 둠. 첫 프레임이 오면 OnFrameReady에서 멈춘다.
        /// </summary>
        private void PrimeBackForLoop()
        {
            if (!_isDoubleBuffering || !_back.Player || !FrontPlayer) return;
            if (_transitionSlot == _back) return;
            // 한 번만 재생하는 페이지는 반복할 일이 없어 미리 준비하지 않는다.
            if (!_isLooping) return;

            string url = FrontPlayer.url;
            if (string.IsNullOrEmpty(url)) return;

            EnsureSlotTexture(_back, _back == _slotA ? "PageMediaPlayer_RT_A" : "PageMediaPlayer_RT_B");
            _back.IsPrimed = false;
            _back.IsPriming = true;
            _back.Player.playbackSpeed = 1f;
            _back.Player.sendFrameReadyEvents = true;

            if (_back.Player.url == url && _back.Player.isPrepared)
            {
                _back.Player.frame = 0;
            }
            else
            {
                _back.Player.url = url;
            }

            _back.Player.Play();
        }

        /// <summary>
        /// Back 플레이어를 멈추고 텍스처를 해제함. 더블 버퍼링을 끌 때 부하와 메모리를 되돌리기 위함.
        /// </summary>
        private void ReleaseBackSlot()
        {
            StopSlot(_back);
            if (_back.Texture)
            {
                if (_back.Player) _back.Player.targetTexture = null;
                _back.Texture.Release();
                DestroyUtil.SafeDestroy(_back.Texture);
                _back.Texture = null;
            }
        }

        /// <summary>
        /// 슬롯에 텍스처가 없으면 만들어 플레이어 출력에 연결함.
        /// </summary>
        private void EnsureSlotTexture(VideoSlot slot, string textureName)
        {
            if (slot.Texture || !slot.Player) return;

            slot.Texture = new RenderTexture(Mathf.Max(2, _renderTextureSize.x), Mathf.Max(2, _renderTextureSize.y), 0, RenderTextureFormat.ARGB32);
            slot.Texture.name = textureName;
            slot.Texture.Create();
            ClearRenderTexture(slot.Texture);
            slot.Player.targetTexture = slot.Texture;
        }

        /// <summary>
        /// 슬롯의 재생을 멈추고 대기 상태를 지움.
        /// </summary>
        private void StopSlot(VideoSlot slot)
        {
            slot.IsPrimed = false;
            slot.IsPriming = false;
            if (!slot.Player) return;

            slot.Player.sendFrameReadyEvents = false;
            slot.Player.playbackSpeed = 1f;
            slot.Player.Stop();
            ClearRenderTexture(slot.Texture);
        }

        /// <summary>
        /// 슬롯의 이벤트 구독을 풀고 플레이어를 멈추며 텍스처를 해제함.
        /// </summary>
        private void ReleaseSlot(VideoSlot slot)
        {
            if (slot == null) return;

            if (slot.Player)
            {
                slot.Player.errorReceived -= OnVideoError;
                slot.Player.frameReady -= OnFrameReady;
                slot.Player.seekCompleted -= OnSeekCompleted;
                slot.Player.loopPointReached -= OnLoopPointReached;
                slot.Player.Stop();
                slot.Player.targetTexture = null;
            }

            if (slot.Texture)
            {
                slot.Texture.Release();
                DestroyUtil.SafeDestroy(slot.Texture);
                slot.Texture = null;
            }
        }

        /// <summary>
        /// 텍스처를 투명색으로 지워 이전 영상의 프레임이 남지 않게 함.
        /// </summary>
        private static void ClearRenderTexture(RenderTexture texture)
        {
            if (!texture) return;

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
        }

        #endregion

        #region A/V Sync

        /// <summary>
        /// TTS가 시작될 때 영상과 TTS를 맞추기 시작함.
        /// <para>
        /// 영상은 자체 시계(Freerun, 실제 시간)로, TTS는 오디오 DSP 시계로 흘러 조금씩 어긋난다.
        /// 남는 작은 차이는 재생 속도를 ±10% 안에서 조절해 서서히 없애고, 크게 벌어진 상태가 이어질 때만 한 번 탐색한다.
        /// ExternalTime 모드는 기준 시간이 크게 뛰면 2초가량 멈추거나 0.1초 늦은 채 고정돼 쓰지 않는다(2026-10-12 측정).
        /// </para>
        /// </summary>
        private void BindVideoToTts()
        {
            _isSyncingToTts = true;
            _smoothedDrift = 0d;
            _isSeeking = false;
            _seekStrikeCount = 0;
            _nextSeekCheckTime = Time.unscaledTime + _seekCheckIntervalSeconds;
        }

        /// <summary>
        /// 동기 맞추기를 끝내고 영상 재생 속도를 원래대로 돌림.
        /// </summary>
        private void ReleaseVideoFromTts()
        {
            _isSyncingToTts = false;
            _isSeeking = false;
            if (_front != null && FrontPlayer) FrontPlayer.playbackSpeed = 1f;
        }

        /// <summary>
        /// 매 프레임 영상과 TTS의 차이를 보고 재생 속도를 조절하거나, 크게 벌어지면 탐색함. TTS가 끝나면 원래 속도로 돌림.
        /// </summary>
        private void SyncVideoToTts()
        {
            if (!FrontPlayer || !_ttsSource || !_ttsSource.isPlaying)
            {
                ReleaseVideoFromTts();
                return;
            }

            // 탐색이 끝나기 전의 time 값은 일시적으로 튀므로 쓰지 않는다.
            if (_isSeeking) return;

            double target = GetVideoTimeForTts();

            // 한 번만 재생하는 영상은 TTS가 영상 끝을 넘으면 맞추기를 끝낸다(끝 프레임에 멈춘 채 둔다).
            if (!_isLooping && FrontPlayer.length > 0d && target >= FrontPlayer.length)
            {
                ReleaseVideoFromTts();
                return;
            }
            // time은 보이는 프레임의 시작 시각이라 평균 반 프레임 늦게 보인다. 프레임 가운데로 보정해 비교한다.
            double drift = WrapDrift(FrontPlayer.time + FrameDuration() * 0.5d - target);
            _smoothedDrift += (drift - _smoothedDrift) * 0.2d;

            if (TrySeekOnLargeDrift(drift, target)) return;

            double adjust = Math.Abs(_smoothedDrift) < _speedDeadZoneSeconds ? 0d : -_smoothedDrift * _speedGainPerSecond;
            if (adjust > _maxSpeedAdjust) adjust = _maxSpeedAdjust;
            if (adjust < -_maxSpeedAdjust) adjust = -_maxSpeedAdjust;
            FrontPlayer.playbackSpeed = (float)(1d + adjust);
        }

        /// <summary>
        /// 차이가 탐색 기준을 넘은 상태가 두 번 연속 확인되면 영상을 TTS 위치로 탐색함. 탐색했으면 true.
        /// </summary>
        private bool TrySeekOnLargeDrift(double drift, double target)
        {
            if (Time.unscaledTime < _nextSeekCheckTime) return false;
            _nextSeekCheckTime = Time.unscaledTime + _seekCheckIntervalSeconds;

            if (Math.Abs(drift) <= _seekThresholdSeconds)
            {
                _seekStrikeCount = 0;
                return false;
            }

            // 멈춤 직후에는 영상이 스스로 따라잡는 중일 수 있어 한 번 더 확인한 뒤에 탐색한다.
            _seekStrikeCount++;
            if (_seekStrikeCount < 2) return false;

            _seekStrikeCount = 0;
            _isSeeking = true;
            _smoothedDrift = 0d;
            FrontPlayer.playbackSpeed = 1f;
            FrontPlayer.time = target;
            if (_logger != null) _logger.ZLogInformation($"[PageMediaPlayer] A/V drift {drift:F3}s. Seeking video to {target:F3}s.");
            return true;
        }

        /// <summary>
        /// 현재 TTS 재생 위치에 해당하는 영상 시간을 계산함. 반복 영상이 TTS보다 짧으면 반복 위치로 접고,
        /// 한 번만 재생하는 영상은 접지 않는다(영상 길이를 넘은 값이 그대로 나온다).
        /// </summary>
        private double GetVideoTimeForTts()
        {
            AudioClip clip = _ttsSource.clip;
            // time(float)보다 정밀한 샘플 위치를 쓴다.
            double ttsTime = clip ? (double)_ttsSource.timeSamples / clip.frequency : _ttsSource.time;
            double videoTime = ttsTime + _videoOffsetSeconds;
            if (videoTime < 0d) videoTime = 0d;

            double length = FrontPlayer.length;
            return _isLooping && length > 0d ? videoTime % length : videoTime;
        }

        /// <summary>
        /// 반복 경계를 사이에 둔 차이를 가장 짧은 거리로 바꿈. 영상 끝(6.99초)과 다음 바퀴 시작(0.01초)을
        /// 7초 차이가 아니라 0.02초 차이로 보게 해, TTS가 영상보다 길 때 경계마다 불필요한 탐색을 막는다.
        /// </summary>
        private double WrapDrift(double drift)
        {
            double length = FrontPlayer.length;
            if (!_isLooping || length <= 0d) return drift;

            double half = length * 0.5d;
            while (drift > half) drift -= length;
            while (drift < -half) drift += length;
            return drift;
        }

        /// <summary>
        /// 영상 한 프레임의 길이(초). 프레임 정보가 없으면 30fps로 가정함.
        /// </summary>
        private double FrameDuration()
        {
            double fps = FrontPlayer.frameRate;
            return fps > 0d ? 1d / fps : 1d / 30d;
        }

        #endregion

        #region VideoPlayer Setup & Events

        /// <summary>
        /// 영상 소리를 끄고 RenderTexture 출력·수동 반복으로 설정하며 이벤트를 한 번만 구독함.
        /// </summary>
        private void ConfigureVideoPlayer(VideoPlayer player)
        {
            player.playOnAwake = false;
            player.source = VideoSource.Url;
            // 자체 반복은 반복 직후 깨진 프레임을 내보내므로 끄고 OnLoopPointReached에서 직접 반복한다.
            player.isLooping = false;
            player.waitForFirstFrame = true;
            player.skipOnDrop = true;
            player.renderMode = VideoRenderMode.RenderTexture;
            // 영상 파일에 음성이 들어 있어도 재생하지 않는다. 음성은 항상 별도 TTS 파일이다.
            player.audioOutputMode = VideoAudioOutputMode.None;
            player.sendFrameReadyEvents = false;
            player.errorReceived += OnVideoError;
            player.frameReady += OnFrameReady;
            player.seekCompleted += OnSeekCompleted;
            player.loopPointReached += OnLoopPointReached;
        }

        /// <summary>
        /// 첫 프레임 이벤트를 처리함. 전환 중인 슬롯이면 화면을 넘기고, 반복 대기 중인 Back이면 첫 프레임에서 멈춘다.
        /// </summary>
        private void OnFrameReady(VideoPlayer source, long frameIdx)
        {
            source.sendFrameReadyEvents = false;

            if (_transitionSlot != null && source == _transitionSlot.Player)
            {
                // 전환이 겹쳐 대기 중인 페이지가 없으면 늦게 도착한 이전 요청의 이벤트이므로 무시한다.
                if (_pendingTtsIndex < 0) return;
                CompleteTransition();
                return;
            }

            if (source == _back.Player && _back.IsPriming)
            {
                source.Pause();
                _back.IsPriming = false;
                _back.IsPrimed = true;
            }
        }

        /// <summary>
        /// 동기 맞추기용 탐색이 끝났음을 기록하고, 다음 탐색 판단을 한 주기 뒤로 미룸.
        /// </summary>
        private void OnSeekCompleted(VideoPlayer source)
        {
            if (source != FrontPlayer) return;

            _isSeeking = false;
            _nextSeekCheckTime = Time.unscaledTime + _seekCheckIntervalSeconds;
        }

        /// <summary>
        /// 영상 오류를 기록함. 전환 중인 슬롯이면 준비 대기를 끝내고, 반복 대기 중이면 대기를 취소한다.
        /// </summary>
        private void OnVideoError(VideoPlayer source, string message)
        {
            if (_transitionSlot != null && source == _transitionSlot.Player) _prepareFailed = true;
            if (source == _back.Player) _back.IsPriming = false;
            if (_logger != null) _logger.ZLogError($"[PageMediaPlayer] Video error: {source.url} / {message}");
        }

        #endregion

        #region TTS Loading

        /// <summary>
        /// 한 페이지의 TTS 파일을 읽어 AudioClip으로 캐시함. 경로가 비어 있으면 건너뜀.
        /// </summary>
        private async UniTask LoadTtsAsync(int pageIndex, string relativePath, CancellationToken cancellationToken)
        {
            AudioClip clip = await LoadAudioClipAsync(relativePath, cancellationToken);
            if (clip) _ttsClips[pageIndex] = clip;
        }

        /// <summary>
        /// StreamingAssets 상대 경로의 오디오 파일을 읽어 AudioClip을 만듦. 경로가 비었거나 실패하면 null.
        /// </summary>
        private async UniTask<AudioClip> LoadAudioClipAsync(string relativePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;

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
                    return null;
                }

                using (LoadTtsMarker.Auto())
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (!clip)
                    {
                        if (_logger != null) _logger.ZLogWarning($"[PageMediaPlayer] TTS clip is null: {relativePath}");
                        return null;
                    }

                    clip.name = Path.GetFileNameWithoutExtension(relativePath);
                    return clip;
                }
            }
        }

        /// <summary>
        /// LoadClipAsync로 읽은 클립을 모두 파괴함.
        /// </summary>
        private void ReleaseExtraClips()
        {
            for (int i = 0; i < _extraClips.Count; i++)
            {
                DestroyUtil.SafeDestroy(_extraClips[i]);
            }

            _extraClips.Clear();
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
