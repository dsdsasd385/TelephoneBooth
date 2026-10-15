using System;
using TMPro;
using UnityEngine;

namespace Idle
{
    /// <summary>
    /// TMP 텍스트에 현재 연도를 넣어 표시함. _format의 {0} 자리에 PC 시계 기준 연도가 들어간다(예: "{0}년" → "2026년").
    /// <para>
    /// 전시가 해를 넘겨 계속 켜져 있어도 바뀌도록, 켜질 때마다 갱신하고 켜져 있는 동안에는 _checkIntervalSeconds마다 연도를 다시 확인한다.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(TMP_Text))]
    public class CurrentYearText : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private TMP_Text _text;
        // {0}에 연도가 들어간다. 줄바꿈·다른 문구와 함께 써도 된다.
        [SerializeField] private string _format = "{0}";
        [SerializeField, Min(1f)] private float _checkIntervalSeconds = 60f;

        #endregion

        #region Private Fields

        private int _shownYear = -1;
        private float _nextCheckTime;

        #endregion

        #region Unity Lifecycle

        private void Reset()
        {
            _text = GetComponent<TMP_Text>();
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextCheckTime) return;
            Refresh();
        }

        #endregion

        #region Year

        /// <summary>
        /// 현재 연도가 표시 중인 연도와 다르면 텍스트를 다시 씀.
        /// </summary>
        private void Refresh()
        {
            _nextCheckTime = Time.unscaledTime + _checkIntervalSeconds;

            int year = DateTime.Now.Year;
            if (year == _shownYear || !_text) return;

            _shownYear = year;
            _text.text = string.Format(_format, year);
        }

        #endregion
    }
}
