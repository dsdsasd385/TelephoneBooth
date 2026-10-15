using System;
using R3;
using TextInput;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Message
{
    /// <summary>
    /// 보내기로 확정한 이름과 다짐.
    /// </summary>
    public readonly struct MessageSubmission
    {
        public readonly string Name;
        public readonly string Message;

        public MessageSubmission(string name, string message)
        {
            Name = name;
            Message = message;
        }
    }

    /// <summary>
    /// 9_Message 다짐 입력 화면(작업계획서 9장 3번)의 이름·다짐 입력칸과 글자 수를 맡는 뷰.
    /// <para>
    /// 입력칸은 TMP_InputField를 쓰되 글자는 화면 키보드로만 넣는다. 키 입력을 지금 고른 칸의 HangulComposer로 조합해
    /// 칸에 써 넣고, 칸을 누르면 그 칸이 입력 대상이 된다. 물리 키보드로 들어온 글자는 조합 결과로 되돌린다.
    /// 키를 누르면 선택이 버튼으로 넘어가 커서가 사라지므로 입력할 때마다 고른 칸을 다시 활성화한다.
    /// </para>
    /// <para>
    /// 규칙: 이름·다짐 최대 글자 수는 설정값이고 다짐 칸 아래에 "12/30" 형식으로 표시한다. 둘 중 하나라도 비었거나
    /// 공백만 있으면 보낼 수 없다. Enter는 이름 칸에서는 다짐 칸으로 넘어가고, 다짐 칸에서는 보내기다(줄바꿈 없음).
    /// 전송·건너뛰기·타임아웃 처리는 씬 매니저가 맡는다.
    /// </para>
    /// </summary>
    public class MessageInputView : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private VirtualKeyboard _keyboard;
        [SerializeField] private TMP_InputField _nameField;
        [SerializeField] private TMP_InputField _messageField;
        [SerializeField] private TMP_Text _messageLengthText;
        // 화면에 보내기 버튼이 있을 때만 연결한다. 없으면 키보드 Enter로만 보낸다.
        [SerializeField] private Button _sendButton;

        #endregion

        #region Private Fields

        private readonly HangulComposer _nameComposer = new HangulComposer();
        private readonly HangulComposer _messageComposer = new HangulComposer();
        private readonly Subject<MessageSubmission> _submitted = new Subject<MessageSubmission>();
        private IDisposable _subscriptions;
        private TMP_InputField _activeField;
        private int _nameMaxLength = 10;
        private int _messageMaxLength = 30;
        private bool _isBound;

        #endregion

        #region Properties

        /// <summary>
        /// 보내기 버튼이나 다짐 칸에서 키보드 Enter로 보낼 수 있는 입력을 확정하면 발행함.
        /// </summary>
        public Observable<MessageSubmission> Submitted => _submitted;

        /// <summary>
        /// 이름·다짐이 모두 보낼 수 있는 상태인지 여부.
        /// </summary>
        public bool IsSendable => IsFilled(_nameComposer.Text) && IsFilled(_messageComposer.Text);

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 구독과 Subject를 정리함.
        /// </summary>
        private void OnDestroy()
        {
            _subscriptions?.Dispose();
            _subscriptions = null;
            _submitted.Dispose();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 키보드·입력칸·보내기 버튼 입력을 연결하고 최대 글자 수를 정함. 씬 매니저가 시작 시 한 번 부른다.
        /// </summary>
        public void Bind(int nameMaxLength, int messageMaxLength)
        {
            if (_isBound) return;
            _isBound = true;
            _nameMaxLength = Mathf.Max(1, nameMaxLength);
            _messageMaxLength = Mathf.Max(1, messageMaxLength);

            DisposableBuilder builder = new DisposableBuilder();

            if (_keyboard)
            {
                _keyboard.Bind();
                _keyboard.CharInput
                    .Subscribe(this, static (c, self) => self.OnCharInput(c))
                    .AddTo(ref builder);
                _keyboard.BackspacePressed
                    .Subscribe(this, static (_, self) => self.OnBackspace())
                    .AddTo(ref builder);
                _keyboard.EnterPressed
                    .Subscribe(this, static (_, self) => self.OnEnter())
                    .AddTo(ref builder);
            }
            else
            {
                Debug.LogError($"[MessageInputView] {name}: VirtualKeyboard is not assigned.");
            }

            BindField(_nameField, nameof(_nameField), ref builder);
            BindField(_messageField, nameof(_messageField), ref builder);

            if (_sendButton)
            {
                _sendButton.OnClickAsObservable()
                    .Subscribe(this, static (_, self) => self.TrySubmit())
                    .AddTo(ref builder);
            }

            _subscriptions = builder.Build();
            ResetInput();
        }

        /// <summary>
        /// 입력을 비우고 자판을 한글로, 입력 대상을 이름 칸으로 되돌림. 화면에 들어올 때와 타임아웃으로 버릴 때 부른다.
        /// </summary>
        public void ResetInput()
        {
            _nameComposer.Clear();
            _messageComposer.Clear();
            if (_keyboard) _keyboard.ResetToKorean();

            _activeField = _nameField;
            RefreshField(_nameField);
            RefreshField(_messageField);
            FocusActiveField();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 입력칸 하나를 화면 키보드 전용으로 설정하고 선택·값 변경을 구독함.
        /// </summary>
        private void BindField(TMP_InputField field, string fieldName, ref DisposableBuilder builder)
        {
            if (!field)
            {
                Debug.LogError($"[MessageInputView] {name}: {fieldName} is not assigned.");
                return;
            }

            // 칸을 다시 눌러도 전체 선택되지 않고, 키를 누르느라 선택이 풀려도 커서 위치를 잃지 않게 한다.
            field.onFocusSelectAll = false;
            field.resetOnDeActivation = false;
            field.characterLimit = 0;
            field.lineType = TMP_InputField.LineType.SingleLine;

            field.onSelect.AsObservable()
                .Subscribe((self: this, field), static (_, state) => state.self.SetActiveField(state.field))
                .AddTo(ref builder);
            // 물리 키보드로 들어온 글자(에디터의 Space 수화기 토글 등)는 조합 결과로 되돌린다.
            field.onValueChanged.AsObservable()
                .Subscribe((self: this, field), static (_, state) => state.self.RefreshField(state.field))
                .AddTo(ref builder);
        }

        private void OnCharInput(char c)
        {
            if (!_activeField) return;

            // 최대 글자 수를 넘는 입력은 무시한다. 받침처럼 길이가 늘지 않는 입력은 받는다.
            if (ComposerOf(_activeField).TryInput(c, MaxLengthOf(_activeField))) RefreshField(_activeField);
            FocusActiveField();
        }

        private void OnBackspace()
        {
            if (!_activeField) return;

            ComposerOf(_activeField).Backspace();
            RefreshField(_activeField);
            FocusActiveField();
        }

        /// <summary>
        /// 이름 칸에서는 다짐 칸으로 넘어가고, 다짐 칸에서는 보내기를 시도함.
        /// </summary>
        private void OnEnter()
        {
            if (_activeField == _nameField && _messageField)
            {
                SetActiveField(_messageField);
                return;
            }

            TrySubmit();
            FocusActiveField();
        }

        /// <summary>
        /// 입력 대상을 바꿈. 떠나는 칸의 조합 중인 음절은 확정한다.
        /// </summary>
        private void SetActiveField(TMP_InputField field)
        {
            if (field == _activeField) return;

            if (_activeField)
            {
                ComposerOf(_activeField).Commit();
                RefreshField(_activeField);
            }

            _activeField = field;
            FocusActiveField();
        }

        /// <summary>
        /// 보낼 수 있으면 조합 중인 음절을 확정하고 이름·다짐을 발행함.
        /// </summary>
        private void TrySubmit()
        {
            if (!IsSendable) return;

            _nameComposer.Commit();
            _messageComposer.Commit();
            _submitted.OnNext(new MessageSubmission(_nameComposer.Text, _messageComposer.Text));
        }

        /// <summary>
        /// 입력칸을 조합 결과로 맞추고 글자 수·보내기 버튼 상태를 갱신함.
        /// </summary>
        private void RefreshField(TMP_InputField field)
        {
            if (!field) return;

            string text = ComposerOf(field).Text;
            if (field.text != text) field.SetTextWithoutNotify(text);
            if (field == _activeField && field.isFocused) field.MoveTextEnd(false);

            if (_messageLengthText) _messageLengthText.text = $"{_messageComposer.Text.Length}/{_messageMaxLength}";
            if (_sendButton) _sendButton.interactable = IsSendable;
        }

        /// <summary>
        /// 입력 대상 칸을 다시 선택·활성화하고 커서를 끝으로 보냄. 키 버튼을 누르면 선택이 버튼으로 넘어가기 때문이다.
        /// </summary>
        private void FocusActiveField()
        {
            if (!_activeField || !_activeField.isActiveAndEnabled) return;

            EventSystem eventSystem = EventSystem.current;
            if (eventSystem && eventSystem.currentSelectedGameObject != _activeField.gameObject)
            {
                eventSystem.SetSelectedGameObject(_activeField.gameObject);
            }

            _activeField.ActivateInputField();
            _activeField.MoveTextEnd(false);
        }

        private HangulComposer ComposerOf(TMP_InputField field)
        {
            return field == _nameField ? _nameComposer : _messageComposer;
        }

        private int MaxLengthOf(TMP_InputField field)
        {
            return field == _nameField ? _nameMaxLength : _messageMaxLength;
        }

        /// <summary>
        /// 비어 있지 않고 공백만으로 이뤄지지 않았는지 여부.
        /// </summary>
        private static bool IsFilled(string text)
        {
            return !string.IsNullOrWhiteSpace(text);
        }

        #endregion
    }
}
