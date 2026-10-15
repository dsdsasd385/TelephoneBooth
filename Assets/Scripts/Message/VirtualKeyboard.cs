using System;
using R3;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Message
{
    /// <summary>
    /// 메시지 입력용 두벌식/영문 화면 키보드(작업계획서 9장).
    /// KoreanLiberationArmy 프로젝트의 KLA.UI.VirtualKeyboard를 이 프로젝트 규칙에 맞게 옮겼다.
    /// <para>
    /// 원본과 다른 점: 키를 코드로 만들지 않고 씬에 배치한 버튼을 인스펙터로 연결한다. 이벤트는 C# event 대신 R3로 발행하고,
    /// 스킨은 StreamingAssets PNG 대신 Sprite로 연결하며, UiSettings·KioskTheme 의존을 없애 색을 인스펙터에서 정한다.
    /// 씬의 Keyboard는 디자인 시안대로 1920×374에 배치했다. 키 스프라이트는 그림자 4px을 포함해 162×78(보이는 키 154×70)이고
    /// Space 1001×78, Enter 239×78, 키 간격 5, 행 간격 7이다.
    /// </para>
    /// <para>
    /// Shift는 원샷이다. 문자를 하나 입력하면 이전 자판으로 돌아오고, 한/영을 바꿔도 풀린다.
    /// 스페이스·지우기는 문자 입력이 아니므로 Shift를 풀지 않는다.
    /// 이 클래스는 키 입력만 알리고 조합은 하지 않는다. 한글 조합은 HangulComposer가 맡는다.
    /// </para>
    /// </summary>
    public class VirtualKeyboard : MonoBehaviour
    {
        #region Types

        /// <summary>
        /// 자판 상태. 스킨 배열 인덱스로 쓰이므로 순서를 바꾸지 말 것.
        /// </summary>
        public enum KeyboardLayer
        {
            Korean = 0,
            KoreanShift = 1,
            English = 2,
            EnglishShift = 3
        }

        #endregion

        #region Constants

        // 문자 키 26개를 왼쪽 위부터 행 순서(10·9·7)로 나열한 자판. _charKeys·_charLabels도 같은 순서로 연결한다.
        private const string KoreanKeys = "ㅂㅈㄷㄱㅅㅛㅕㅑㅐㅔ" + "ㅁㄴㅇㄹㅎㅗㅓㅏㅣ" + "ㅋㅌㅊㅍㅠㅜㅡ";
        // Shift 상태의 두벌식. 쌍자음(ㅃㅉㄸㄲㅆ)과 ㅒㅖ만 달라진다.
        private const string KoreanShiftKeys = "ㅃㅉㄸㄲㅆㅛㅕㅑㅒㅖ" + "ㅁㄴㅇㄹㅎㅗㅓㅏㅣ" + "ㅋㅌㅊㅍㅠㅜㅡ";
        private const string EnglishKeys = "qwertyuiop" + "asdfghjkl" + "zxcvbnm";
        private const string EnglishShiftKeys = "QWERTYUIOP" + "ASDFGHJKL" + "ZXCVBNM";

        private const int CharKeyCount = 26;
        private const int LayerCount = 4;
        private const string CapsLabel = "CAPS";

        #endregion

        #region Serialized Fields

        // 문자 키 버튼과 그 라벨. 왼쪽 위부터 행 순서(10·9·7)로 26개를 연결한다.
        [SerializeField] private Button[] _charKeys = Array.Empty<Button>();
        [SerializeField] private TMP_Text[] _charLabels = Array.Empty<TMP_Text>();

        [SerializeField] private Button _capsKey;
        [SerializeField] private TMP_Text _capsLabel;
        [SerializeField] private Button _backspaceKey;
        [SerializeField] private Button _languageKey;
        [SerializeField] private Button _spaceKey;
        [SerializeField] private Button _enterKey;

        // 켜져 있음을 보여 주는 채움 이미지(키 안쪽, 라벨 뒤). Caps는 Shift가 켜졌을 때, 한/영은 영문 자판일 때 보인다.
        // 키 스프라이트 안쪽이 반투명이라 키 Image 색을 바꾸면 곱해져 거의 보이지 않으므로 채움을 따로 둔다.
        [SerializeField] private GameObject _capsActiveFill;
        [SerializeField] private GameObject _languageActiveFill;

        // 자판 상태별 배경 스킨(KeyboardLayer 순서). 4장이 모두 있을 때만 스킨 모드로 동작하고,
        // 스킨 모드에서는 키 판·라벨을 감추고 버튼은 투명한 터치 영역으로만 쓴다.
        [SerializeField] private Image _skinImage;
        [SerializeField] private Sprite[] _layerSkins = new Sprite[LayerCount];

        #endregion

        #region Private Fields

        private readonly Subject<char> _charInput = new Subject<char>();
        private readonly Subject<Unit> _backspacePressed = new Subject<Unit>();
        private readonly Subject<Unit> _enterPressed = new Subject<Unit>();

        private Image[] _keyPlates = Array.Empty<Image>();
        private Color[] _plateHomeColors = Array.Empty<Color>();
        private IDisposable _buttonSubscriptions;
        private bool _isBound;
        private bool _isEnglish;
        private bool _isShift;

        #endregion

        #region Properties

        /// <summary>
        /// 문자 키(자모 또는 영문 한 글자)나 스페이스(' ')를 누르면 그 글자를 발행함.
        /// </summary>
        public Observable<char> CharInput => _charInput;

        /// <summary>
        /// 지우기 키를 누르면 발행함.
        /// </summary>
        public Observable<Unit> BackspacePressed => _backspacePressed;

        /// <summary>
        /// Enter 키를 누르면 발행함. 메시지 화면은 이것을 "보내기"로 쓴다(줄바꿈 없음).
        /// </summary>
        public Observable<Unit> EnterPressed => _enterPressed;

        /// <summary>
        /// 현재 자판 상태.
        /// </summary>
        public KeyboardLayer CurrentLayer => _isEnglish
            ? (_isShift ? KeyboardLayer.EnglishShift : KeyboardLayer.English)
            : (_isShift ? KeyboardLayer.KoreanShift : KeyboardLayer.Korean);

        /// <summary>
        /// 스킨 4장이 모두 연결됐는지 여부.
        /// </summary>
        public bool IsSkinReady
        {
            get
            {
                if (!_skinImage || _layerSkins == null || _layerSkins.Length < LayerCount) return false;
                for (int i = 0; i < LayerCount; i++)
                {
                    if (!_layerSkins[i]) return false;
                }
                return true;
            }
        }

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// 버튼 구독과 Subject를 정리함.
        /// </summary>
        private void OnDestroy()
        {
            _buttonSubscriptions?.Dispose();
            _buttonSubscriptions = null;
            _charInput.Dispose();
            _backspacePressed.Dispose();
            _enterPressed.Dispose();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// 키 버튼 입력을 연결하고 씬에서 정한 키 색을 기억함. 화면이 꺼져 있어도 동작하도록 시작 시 한 번 부른다.
        /// </summary>
        public void Bind()
        {
            if (_isBound) return;
            _isBound = true;

            if (_charKeys.Length != CharKeyCount || _charLabels.Length != CharKeyCount)
            {
                Debug.LogError($"[VirtualKeyboard] {name}: assign {CharKeyCount} char keys and labels in row order (keys {_charKeys.Length}, labels {_charLabels.Length}).");
            }

            DisposableBuilder builder = new DisposableBuilder();

            for (int i = 0; i < _charKeys.Length; i++)
            {
                Button key = _charKeys[i];
                if (!key)
                {
                    Debug.LogError($"[VirtualKeyboard] {name}: char key {i} is not assigned.");
                    continue;
                }

                int index = i;
                key.OnClickAsObservable()
                    .Subscribe(_ => EmitChar(index))
                    .AddTo(ref builder);
            }

            BindKey(_capsKey, nameof(_capsKey), ToggleShift, ref builder);
            BindKey(_languageKey, nameof(_languageKey), ToggleLanguage, ref builder);
            BindKey(_backspaceKey, nameof(_backspaceKey), () => _backspacePressed.OnNext(Unit.Default), ref builder);
            // 스페이스는 문자 입력이 아니므로 Shift를 풀지 않는다.
            BindKey(_spaceKey, nameof(_spaceKey), () => _charInput.OnNext(' '), ref builder);
            BindKey(_enterKey, nameof(_enterKey), () => _enterPressed.OnNext(Unit.Default), ref builder);

            _buttonSubscriptions = builder.Build();

            RememberPlateColors();
            RefreshLayer();
        }

        /// <summary>
        /// 자판을 한글·Shift 꺼짐으로 되돌림. 앞 관람객이 바꾼 자판이 남지 않도록 화면에 들어올 때마다 부른다.
        /// </summary>
        public void ResetToKorean()
        {
            _isEnglish = false;
            _isShift = false;
            RefreshLayer();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 기능 키 하나의 클릭을 연결함. 연결되지 않은 키는 로그만 남기고 건너뛴다.
        /// </summary>
        private void BindKey(Button key, string fieldName, Action onClick, ref DisposableBuilder builder)
        {
            if (!key)
            {
                Debug.LogError($"[VirtualKeyboard] {name}: {fieldName} is not assigned.");
                return;
            }

            key.OnClickAsObservable()
                .Subscribe(_ => onClick())
                .AddTo(ref builder);
        }

        /// <summary>
        /// 씬에서 정한 키 판 색을 기억함. 스킨 모드에서 투명하게 비웠다가 되돌릴 때 쓴다.
        /// </summary>
        private void RememberPlateColors()
        {
            Button[] functionKeys = { _capsKey, _backspaceKey, _languageKey, _spaceKey, _enterKey };
            _keyPlates = new Image[_charKeys.Length + functionKeys.Length];
            _plateHomeColors = new Color[_keyPlates.Length];

            for (int i = 0; i < _keyPlates.Length; i++)
            {
                Button key = i < _charKeys.Length ? _charKeys[i] : functionKeys[i - _charKeys.Length];
                Image plate = key ? key.image : null;
                _keyPlates[i] = plate;
                _plateHomeColors[i] = plate ? plate.color : Color.white;
            }
        }

        /// <summary>
        /// 현재 자판의 글자를 발행하고 원샷 Shift를 풂.
        /// </summary>
        private void EmitChar(int index)
        {
            _charInput.OnNext(CharAt(index));

            if (_isShift)
            {
                _isShift = false;
                RefreshLayer();
            }
        }

        /// <summary>
        /// 현재 자판 상태에서 index번째 문자 키의 글자.
        /// </summary>
        private char CharAt(int index)
        {
            if (_isEnglish) return (_isShift ? EnglishShiftKeys : EnglishKeys)[index];
            return (_isShift ? KoreanShiftKeys : KoreanKeys)[index];
        }

        private void ToggleShift()
        {
            _isShift = !_isShift;
            RefreshLayer();
        }

        private void ToggleLanguage()
        {
            _isEnglish = !_isEnglish;
            _isShift = false;
            RefreshLayer();
        }

        /// <summary>
        /// 현재 자판 상태에 맞춰 스킨과 키 라벨·색을 다시 그림.
        /// </summary>
        private void RefreshLayer()
        {
            bool isSkin = IsSkinReady;

            if (_skinImage)
            {
                _skinImage.gameObject.SetActive(isSkin);
                if (isSkin) _skinImage.sprite = _layerSkins[(int)CurrentLayer];
            }

            for (int i = 0; i < _keyPlates.Length; i++)
            {
                Image plate = _keyPlates[i];
                if (plate) plate.color = isSkin ? Color.clear : _plateHomeColors[i];
            }

            int labelCount = Mathf.Min(_charLabels.Length, CharKeyCount);
            for (int i = 0; i < labelCount; i++)
            {
                TMP_Text label = _charLabels[i];
                if (label) label.text = isSkin ? string.Empty : CharAt(i).ToString();
            }

            RefreshToggleKeys(isSkin);
        }

        /// <summary>
        /// Caps(Shift 켜짐)·한/영(영문 자판)이 켜져 있으면 채움 이미지를 보여 줌. 스킨 모드에서는 그림이 상태를 표현한다.
        /// </summary>
        private void RefreshToggleKeys(bool isSkin)
        {
            if (_capsLabel) _capsLabel.text = isSkin ? string.Empty : CapsLabel;
            if (_capsActiveFill) _capsActiveFill.SetActive(!isSkin && _isShift);
            if (_languageActiveFill) _languageActiveFill.SetActive(!isSkin && _isEnglish);
        }

        #endregion
    }
}
