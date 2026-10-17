using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DG.Tweening;
using R3;
using UnityEngine;
using UnityEngine.UI;

namespace Mission
{
    /// <summary>
    /// 지도 아웃라인 연출에서 장소 버튼을 보여 주는 방식.
    /// </summary>
    public enum MapButtonReveal
    {
        // 같은 순번의 아웃라인과 함께 버튼을 하나씩 페이드인한다.
        WithOutline,
        // 아웃라인이 모두 나타난 뒤 버튼을 한꺼번에 페이드인한다.
        AfterOutlines
    }

    /// <summary>
    /// 미션3 지도 하나(Guide2의 Title1MapImage, SelectScreen의 Title2MapImage). 장소 아웃라인 5개와 장소 버튼 5개를 가진다.
    /// <para>
    /// PlayOutlinesAsync는 아웃라인을 차례로 페이드인하고, 버튼은 _buttonReveal에 따라 아웃라인과 함께 하나씩 또는 아웃라인이 다 나온 뒤
    /// 한꺼번에 페이드인한다. ResetState(true)는 아웃라인·버튼을 바로 보여 준다. 버튼 페이드는 버튼 Image 알파로 한다.
    /// 버튼을 누르면 그 버튼의 첫 자식(선택 표시)만 켜고 Clicked로 버튼을 알린다. 입력 막기는 CanvasGroup.blocksRaycasts로 해서
    /// 버튼이 비활성 색으로 바뀌지 않는다. 연출용 지도(Title1MapImage)는 _isInteractable을 꺼 두어 항상 막혀 있다.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class CipherMapView : MonoBehaviour
    {
        #region Serialized Fields

        // 끄면 버튼 입력을 받지 않는다(Title1MapImage용).
        [SerializeField] private bool _isInteractable = true;
        // 나타나는 순서대로.
        [SerializeField] private Graphic[] _outlines = Array.Empty<Graphic>();
        [SerializeField] private Button[] _buttons = Array.Empty<Button>();

        [Header("아웃라인 연출")]
        // 아웃라인 하나가 나타나기 시작한 뒤 다음 아웃라인이 시작하기까지의 시간(초).
        [SerializeField, Min(0f)] private float _outlineIntervalSeconds = 0.6f;
        [SerializeField, Min(0f)] private float _outlineFadeSeconds = 0.4f;
        [SerializeField] private MapButtonReveal _buttonReveal = MapButtonReveal.WithOutline;
        [SerializeField, Min(0f)] private float _buttonFadeSeconds = 0.4f;

        #endregion

        #region Private Fields

        private readonly Subject<Button> _clicked = new Subject<Button>();
        private IDisposable _subscriptions;
        private CanvasGroup _group;
        private Sequence _outlineSequence;
        private float[] _outlineAlphas = Array.Empty<float>();
        // 버튼 Image와 씬 알파. _buttons와 같은 순서이며 Image가 없는 버튼은 null.
        private Graphic[] _buttonGraphics = Array.Empty<Graphic>();
        private float[] _buttonAlphas = Array.Empty<float>();
        // MorphTo가 옮기는 요소(지도, 아웃라인, 버튼 순)와 씬 배치 그대로의 offset. RestoreLayout이 되돌린다.
        private RectTransform[] _layoutRects = Array.Empty<RectTransform>();
        private Vector2[] _layoutOffsetMins = Array.Empty<Vector2>();
        private Vector2[] _layoutOffsetMaxs = Array.Empty<Vector2>();
        // MorphTo 진행 중 매 프레임 쓰는 버퍼. 요소별 시작·목표 월드 모서리(왼쪽 아래, 오른쪽 위).
        private Vector3[] _morphFromMin = Array.Empty<Vector3>();
        private Vector3[] _morphFromMax = Array.Empty<Vector3>();
        private Vector3[] _morphToMin = Array.Empty<Vector3>();
        private Vector3[] _morphToMax = Array.Empty<Vector3>();
        private readonly Vector3[] _corners = new Vector3[4];
        private float _morphProgress;
        private bool _isInputEnabled;
        private bool _isCached;

        #endregion

        #region Properties

        /// <summary>
        /// 입력이 열려 있을 때 장소 버튼을 누를 때마다 그 버튼을 발행함.
        /// </summary>
        public Observable<Button> Clicked => _clicked;

        public RectTransform RectTransform => (RectTransform)transform;

        /// <summary>
        /// 지도 전체 투명도·입력 막기에 쓰는 CanvasGroup.
        /// </summary>
        public CanvasGroup Group
        {
            get
            {
                CacheDefaults();
                return _group;
            }
        }

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 아웃라인의 씬 알파를 기억함.
        /// </summary>
        private void Awake()
        {
            CacheDefaults();
        }

        /// <summary>
        /// 구독과 연출을 정리함.
        /// </summary>
        private void OnDestroy()
        {
            KillOutlines();
            _subscriptions?.Dispose();
            _clicked.Dispose();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 장소 버튼 클릭을 구독함. 씬 매니저가 시작할 때 한 번 부른다.
        /// </summary>
        public void Bind()
        {
            if (_subscriptions != null) return;

            DisposableBuilder builder = new DisposableBuilder();
            for (int i = 0; i < _buttons.Length; i++)
            {
                Button button = _buttons[i];
                if (!button) continue;

                button.OnClickAsObservable()
                    .Subscribe(_ => OnButtonClicked(button))
                    .AddTo(ref builder);
            }

            _subscriptions = builder.Build();
        }

        /// <summary>
        /// 처음 상태로 돌림. 선택 표시 끔, 입력 막힘, 아웃라인·버튼은 isVisible이면 보이고 아니면 투명.
        /// </summary>
        public void ResetState(bool isVisible)
        {
            CacheDefaults();
            KillOutlines();
            ClearSelection();
            SetInputEnabled(false);

            for (int i = 0; i < _outlines.Length; i++)
            {
                SetAlpha(_outlines[i], isVisible ? _outlineAlphas[i] : 0f);
            }

            for (int i = 0; i < _buttonGraphics.Length; i++)
            {
                SetAlpha(_buttonGraphics[i], isVisible ? _buttonAlphas[i] : 0f);
            }
        }

        /// <summary>
        /// 아웃라인을 순서대로 페이드인하고 버튼을 _buttonReveal 방식으로 페이드인함. 취소되면 그 상태에 둔다(ResetState가 되돌림).
        /// </summary>
        public async UniTask PlayOutlinesAsync(CancellationToken cancellationToken)
        {
            CacheDefaults();
            KillOutlines();

            Sequence sequence = DOTween.Sequence().SetUpdate(true);
            float lastOutlineEnd = 0f;
            for (int i = 0; i < _outlines.Length; i++)
            {
                Graphic outline = _outlines[i];
                if (!outline) continue;

                float start = i * _outlineIntervalSeconds;
                SetAlpha(outline, 0f);
                sequence = sequence.Insert(start, outline.DOFade(_outlineAlphas[i], _outlineFadeSeconds));
                lastOutlineEnd = start + _outlineFadeSeconds;
            }

            for (int i = 0; i < _buttonGraphics.Length; i++)
            {
                Graphic graphic = _buttonGraphics[i];
                if (!graphic) continue;

                float start = _buttonReveal == MapButtonReveal.WithOutline ? i * _outlineIntervalSeconds : lastOutlineEnd;
                SetAlpha(graphic, 0f);
                sequence = sequence.Insert(start, graphic.DOFade(_buttonAlphas[i], _buttonFadeSeconds));
            }

            _outlineSequence = sequence;
            await sequence.ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, cancellationToken);
        }

        /// <summary>
        /// 지도·아웃라인·버튼을 target 지도의 같은 순번 요소 위치·크기로 각각 옮기는 트윈을 만듦(Scale은 1 그대로).
        /// 요소마다 월드 사각형을 보간해 offset으로 적용하므로, 두 지도의 앵커·안쪽 배치가 달라도 끝나는 순간 target과 정확히 겹친다.
        /// target은 시작할 때의 배치를 목표로 쓴다. 되돌리기는 RestoreLayout.
        /// </summary>
        public Tween MorphTo(CipherMapView target, float seconds, Ease ease)
        {
            CacheDefaults();
            if (!target) return null;

            target.CacheDefaults();
            int count = Mathf.Min(_layoutRects.Length, target._layoutRects.Length);
            if (_layoutRects.Length != target._layoutRects.Length)
            {
                Debug.LogWarning($"[CipherMapView] Element count mismatch. {name}={_layoutRects.Length}, {target.name}={target._layoutRects.Length}");
            }

            _morphFromMin = new Vector3[count];
            _morphFromMax = new Vector3[count];
            _morphToMin = new Vector3[count];
            _morphToMax = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                GetWorldRect(_layoutRects[i], out _morphFromMin[i], out _morphFromMax[i]);
                GetWorldRect(target._layoutRects[i], out _morphToMin[i], out _morphToMax[i]);
            }

            _morphProgress = 0f;
            return DOTween.To(GetMorphProgress, SetMorphProgress, 1f, seconds).SetEase(ease);
        }

        /// <summary>
        /// MorphTo로 바꾼 지도·아웃라인·버튼을 씬 배치로 되돌림.
        /// </summary>
        public void RestoreLayout()
        {
            CacheDefaults();
            for (int i = 0; i < _layoutRects.Length; i++)
            {
                RectTransform rect = _layoutRects[i];
                if (!rect) continue;

                rect.offsetMin = _layoutOffsetMins[i];
                rect.offsetMax = _layoutOffsetMaxs[i];
            }
        }

        /// <summary>
        /// 버튼 입력을 열거나 막음. _isInteractable이 꺼진 지도는 항상 막는다.
        /// </summary>
        public void SetInputEnabled(bool isEnabled)
        {
            CacheDefaults();
            _isInputEnabled = isEnabled && _isInteractable;
            _group.blocksRaycasts = _isInputEnabled;
        }

        /// <summary>
        /// 모든 버튼의 선택 표시를 끔.
        /// </summary>
        public void ClearSelection()
        {
            for (int i = 0; i < _buttons.Length; i++)
            {
                SetMarker(_buttons[i], false);
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 누른 버튼의 선택 표시만 켜고 발행함.
        /// </summary>
        private void OnButtonClicked(Button button)
        {
            if (!_isInputEnabled) return;

            for (int i = 0; i < _buttons.Length; i++)
            {
                SetMarker(_buttons[i], _buttons[i] == button);
            }

            _clicked.OnNext(button);
        }

        /// <summary>
        /// 버튼의 첫 자식(선택 표시)을 켜거나 끔.
        /// </summary>
        private static void SetMarker(Button button, bool isOn)
        {
            if (!button || button.transform.childCount == 0) return;

            button.transform.GetChild(0).gameObject.SetActive(isOn);
        }

        /// <summary>
        /// 그래픽의 알파만 바꿈.
        /// </summary>
        private static void SetAlpha(Graphic graphic, float alpha)
        {
            if (!graphic) return;

            Color color = graphic.color;
            color.a = alpha;
            graphic.color = color;
        }

        /// <summary>
        /// 처음 한 번 CanvasGroup과 아웃라인·버튼 Image의 씬 알파를 기억함. 꺼진 화면에서 Awake보다 먼저 불릴 수 있어 따로 둔다.
        /// </summary>
        private void CacheDefaults()
        {
            if (_isCached) return;

            _group = GetComponent<CanvasGroup>();
            _outlineAlphas = new float[_outlines.Length];
            for (int i = 0; i < _outlines.Length; i++)
            {
                _outlineAlphas[i] = _outlines[i] ? _outlines[i].color.a : 1f;
            }

            _buttonGraphics = new Graphic[_buttons.Length];
            _buttonAlphas = new float[_buttons.Length];
            for (int i = 0; i < _buttons.Length; i++)
            {
                _buttonGraphics[i] = _buttons[i] ? _buttons[i].image : null;
                _buttonAlphas[i] = _buttonGraphics[i] ? _buttonGraphics[i].color.a : 1f;
            }

            // 부모가 먼저 옮겨져야 자식의 부모 기준 좌표가 맞으므로 지도 → 아웃라인 → 버튼 순서로 둔다.
            _layoutRects = new RectTransform[1 + _outlines.Length + _buttons.Length];
            _layoutRects[0] = (RectTransform)transform;
            for (int i = 0; i < _outlines.Length; i++)
            {
                _layoutRects[1 + i] = _outlines[i] ? _outlines[i].rectTransform : null;
            }

            for (int i = 0; i < _buttons.Length; i++)
            {
                _layoutRects[1 + _outlines.Length + i] = _buttons[i] ? (RectTransform)_buttons[i].transform : null;
            }

            _layoutOffsetMins = new Vector2[_layoutRects.Length];
            _layoutOffsetMaxs = new Vector2[_layoutRects.Length];
            for (int i = 0; i < _layoutRects.Length; i++)
            {
                if (!_layoutRects[i]) continue;

                _layoutOffsetMins[i] = _layoutRects[i].offsetMin;
                _layoutOffsetMaxs[i] = _layoutRects[i].offsetMax;
            }

            _isCached = true;
        }

        /// <summary>
        /// MorphTo 트윈의 진행도(0–1). DOTween.To의 getter.
        /// </summary>
        private float GetMorphProgress()
        {
            return _morphProgress;
        }

        /// <summary>
        /// 진행도에 맞춰 요소마다 시작·목표 월드 사각형을 보간해 적용함. DOTween.To의 setter.
        /// </summary>
        private void SetMorphProgress(float progress)
        {
            _morphProgress = progress;
            for (int i = 0; i < _morphFromMin.Length; i++)
            {
                RectTransform rect = _layoutRects[i];
                if (!rect) continue;

                SetWorldRect(rect,
                    Vector3.LerpUnclamped(_morphFromMin[i], _morphToMin[i], progress),
                    Vector3.LerpUnclamped(_morphFromMax[i], _morphToMax[i], progress));
            }
        }

        /// <summary>
        /// RectTransform의 월드 사각형(왼쪽 아래, 오른쪽 위)을 구함. 없으면 원점.
        /// </summary>
        private void GetWorldRect(RectTransform rect, out Vector3 min, out Vector3 max)
        {
            if (!rect)
            {
                min = Vector3.zero;
                max = Vector3.zero;
                return;
            }

            rect.GetWorldCorners(_corners);
            min = _corners[0];
            max = _corners[2];
        }

        /// <summary>
        /// 월드 사각형을 부모 기준 좌표로 바꿔 앵커와 상관없이 offsetMin·offsetMax로 적용함. 회전·Scale이 없는 UI 기준.
        /// </summary>
        private static void SetWorldRect(RectTransform rect, Vector3 worldMin, Vector3 worldMax)
        {
            RectTransform parent = rect.parent as RectTransform;
            if (!parent) return;

            Vector2 localMin = parent.InverseTransformPoint(worldMin);
            Vector2 localMax = parent.InverseTransformPoint(worldMax);
            Rect parentRect = parent.rect;
            Vector2 anchorMin = parentRect.min + Vector2.Scale(rect.anchorMin, parentRect.size);
            Vector2 anchorMax = parentRect.min + Vector2.Scale(rect.anchorMax, parentRect.size);
            rect.offsetMin = localMin - anchorMin;
            rect.offsetMax = localMax - anchorMax;
        }

        /// <summary>
        /// 진행 중인 아웃라인 연출을 멈춤.
        /// </summary>
        private void KillOutlines()
        {
            if (_outlineSequence != null && _outlineSequence.IsActive()) _outlineSequence.Kill();
            _outlineSequence = null;
        }

        #endregion
    }
}
