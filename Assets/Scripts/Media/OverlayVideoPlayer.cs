using System;
using System.IO;
using R3;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Media
{
    /// <summary>
    /// 화면 위에 덮는 영상 하나를 정한 횟수만큼 이어서 재생하는 재생기(9_Message 로딩 영상 등).
    /// 여러 번 재생할 때는 VideoPlayer 반복(isLooping)으로 이어 붙여 사이에 화면이 비지 않고, 마지막 회차에서 반복을 꺼 마지막 프레임에 멈춘다.
    /// <para>
    /// 잔여 프레임 방지: 재생을 시작할 때 RenderTexture를 비우고 화면(RawImage)을 끈 채로 준비하며, 새 영상의 첫 프레임이
    /// 나온 시점(frameReady)에 화면을 켠다. 멈출 때도 텍스처를 비우고 화면을 끈다. 그래서 이전 재생의 마지막 프레임이나
    /// 빈 텍스처(흰 화면)가 보이지 않는다. PageMediaPlayer와 같은 방식이다.
    /// </para>
    /// <para>
    /// 영상 소리는 끈다(음성은 항상 별도 파일). 정한 횟수를 끝까지 재생하거나 재생 오류가 나면 Completed를 발행해 흐름이 멈추지 않게 한다.
    /// 화면이 켜져 있는 동안 RawImage가 터치를 막는다.
    /// </para>
    /// </summary>
    public class OverlayVideoPlayer : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private VideoPlayer _player;
        [SerializeField] private RawImage _surface;
        [SerializeField] private Vector2Int _renderTextureSize = new Vector2Int(1920, 1080);

        #endregion

        #region Private Fields

        private readonly Subject<Unit> _completed = new Subject<Unit>();
        private RenderTexture _texture;
        private bool _isConfigured;
        private bool _isPlaying;
        // 이번 재생에서 남은 회차(지금 재생 중인 회차 포함).
        private int _remainingPlays;

        #endregion

        #region Properties

        /// <summary>
        /// 영상을 정한 횟수만큼 끝까지 재생했거나 재생에 실패하면 발행함.
        /// </summary>
        public Observable<Unit> Completed => _completed;

        /// <summary>
        /// 재생 중(준비 중 포함)인지 여부.
        /// </summary>
        public bool IsPlaying => _isPlaying;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            Configure();
        }

        /// <summary>
        /// 이벤트 구독을 풀고 RenderTexture와 Subject를 해제함.
        /// </summary>
        private void OnDestroy()
        {
            if (_player)
            {
                _player.frameReady -= OnFrameReady;
                _player.loopPointReached -= OnLoopPointReached;
                _player.errorReceived -= OnVideoError;
            }

            if (_texture)
            {
                _texture.Release();
                Destroy(_texture);
                _texture = null;
            }

            _completed.Dispose();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// StreamingAssets 기준 상대 경로의 영상을 처음부터 playCount번(1 미만이면 1번) 이어서 재생함. 첫 프레임이 나오면 화면을 켠다.
        /// </summary>
        public void Play(string relativePath, int playCount = 1)
        {
            Configure();
            if (!_player || !_surface) return;

            if (string.IsNullOrEmpty(relativePath))
            {
                Debug.LogError($"[OverlayVideoPlayer] {name}: video path is empty.");
                _completed.OnNext(Unit.Default);
                return;
            }

            Stop();

            // 씬에서 꺼 둔 채 저장해도 재생되게 한다. 화면(RawImage)은 첫 프레임까지 꺼져 있다.
            if (!gameObject.activeSelf) gameObject.SetActive(true);

            _isPlaying = true;
            _remainingPlays = Mathf.Max(1, playCount);
            _player.isLooping = _remainingPlays > 1;
            _player.url = Path.Combine(Application.streamingAssetsPath, relativePath).Replace("\\", "/");
            _player.sendFrameReadyEvents = true;
            _player.Play();
        }

        /// <summary>
        /// 재생을 멈추고 텍스처를 비운 뒤 화면을 끔.
        /// </summary>
        public void Stop()
        {
            _isPlaying = false;

            if (_player)
            {
                _player.sendFrameReadyEvents = false;
                _player.Stop();
            }

            ClearTexture();
            if (_surface) _surface.enabled = false;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// VideoPlayer를 RenderTexture 출력·소리 없음·한 번 재생으로 설정하고 화면을 꺼 둠. 한 번만 실행한다.
        /// </summary>
        private void Configure()
        {
            if (_isConfigured) return;
            _isConfigured = true;

            if (!_player || !_surface)
            {
                Debug.LogError($"[OverlayVideoPlayer] {name}: VideoPlayer or RawImage is not assigned.");
                return;
            }

            _texture = new RenderTexture(Mathf.Max(2, _renderTextureSize.x), Mathf.Max(2, _renderTextureSize.y), 0, RenderTextureFormat.ARGB32);
            _texture.Create();
            ClearTexture();

            _player.playOnAwake = false;
            _player.source = VideoSource.Url;
            _player.isLooping = false;
            _player.waitForFirstFrame = true;
            _player.skipOnDrop = true;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _texture;
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.sendFrameReadyEvents = false;
            _player.frameReady += OnFrameReady;
            _player.loopPointReached += OnLoopPointReached;
            _player.errorReceived += OnVideoError;

            _surface.texture = _texture;
            _surface.enabled = false;
        }

        /// <summary>
        /// 첫 프레임이 나오면 화면을 켬. 이후 프레임 이벤트는 끈다.
        /// </summary>
        private void OnFrameReady(VideoPlayer source, long frameIdx)
        {
            source.sendFrameReadyEvents = false;
            if (_isPlaying && _surface) _surface.enabled = true;
        }

        private void OnLoopPointReached(VideoPlayer source)
        {
            if (!_isPlaying) return;

            // 반복 중이면 이미 처음부터 다시 재생되고 있다. 마지막 회차에 들어가면 반복을 꺼 끝 프레임에 멈추게 한다.
            if (_remainingPlays > 1)
            {
                _remainingPlays--;
                if (_remainingPlays == 1) source.isLooping = false;
                return;
            }

            _isPlaying = false;
            _completed.OnNext(Unit.Default);
        }

        private void OnVideoError(VideoPlayer source, string message)
        {
            Debug.LogError($"[OverlayVideoPlayer] {name}: failed to play {source.url}: {message}");
            Stop();
            _completed.OnNext(Unit.Default);
        }

        /// <summary>
        /// 텍스처를 투명색으로 지워 이전 영상의 프레임이 남지 않게 함.
        /// </summary>
        private void ClearTexture()
        {
            if (!_texture) return;

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _texture;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
        }

        #endregion
    }
}
