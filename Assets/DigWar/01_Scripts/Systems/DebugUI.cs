using UnityEngine;
using Core;

namespace Systems
{
    /// <summary>
    /// 우측 상단 디버그 정보 표시.
    /// IMGUI(OnGUI) 기반이므로 Canvas 없이 즉시 동작한다.
    /// 빌드에서 제외하려면 오브젝트 비활성화 또는 #if UNITY_EDITOR로 감싸면 된다.
    /// </summary>
    public class DebugUI : MonoBehaviour
    {
        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;
        private bool _stylesReady;

        private Player.PlayerController _player;
        private Tunnel.TunnelGenerator _tunnel;

        private void Start()
        {
            _player = FindObjectOfType<Player.PlayerController>();
            _tunnel = FindObjectOfType<Tunnel.TunnelGenerator>();
        }

        private void InitStyles()
        {
            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 16,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(12, 12, 10, 10)
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                richText = true,
                normal = { textColor = Color.white }
            };

            _stylesReady = true;
        }

        private void OnGUI()
        {
            if (!_stylesReady) InitStyles();

            float w = 320f;
            float h = 320f;
            float margin = 10f;
            Rect panelRect = new Rect(Screen.width - w - margin, margin, w, h);

            GUI.Box(panelRect, "", _boxStyle);
            GUILayout.BeginArea(new Rect(panelRect.x + 10, panelRect.y + 8, w - 20, h - 16));

            Label("<b>— DEBUG —</b>");

            // 점수
            float score = GameManager.Instance != null ? GameManager.Instance.CurrentScore : 0f;
            Label($"점수: <color=yellow>{score:F0}</color>");

            // 플레이어 스케일
            float scale = _player != null ? _player.transform.localScale.x : 0f;
            Label($"크기: <color=cyan>{scale:F2}</color>");

            // 속도
            if (_player != null && GameManager.Instance != null)
            {
                var settings = GameManager.Instance.Settings;
                float baseSpeed = settings.BaseSpeed * scale;
                float boostSpeed = baseSpeed * settings.BoostMultiplier;
                Label($"속도: <color=white>{baseSpeed:F1}</color>");
                Label($"부스트 속도: <color=orange>{boostSpeed:F1}</color>");
            }

            // 터널 정보
            if (_tunnel != null)
            {
                Label($"터널 포인트: <color=lime>{_tunnel.TotalPointCount}</color>");
                Label($"터널 세그먼트: <color=lime>{_tunnel.SegmentCount}</color>");
            }

            // 카메라 줌
            if (Camera.main != null)
            {
                Label($"카메라 줌: <color=#88CCFF>{Camera.main.orthographicSize:F1}</color>");
            }

            // FPS
            Label($"FPS: <color=green>{1f / Time.unscaledDeltaTime:F0}</color>");

            // 맵 경계 거리
            if (_player != null && GameManager.Instance != null)
            {
                float dist = _player.transform.position.magnitude;
                float radius = GameManager.Instance.Settings.MapRadius;
                float remaining = radius - dist;
                string distColor = remaining < GameManager.Instance.Settings.MapWarningZone ? "red" : "#AAFFAA";
                Label($"경계까지: <color={distColor}>{remaining:F1}</color>");
            }

            // 게임 상태
            bool alive = GameManager.Instance != null && GameManager.Instance.IsGameActive;
            Label($"상태: {(alive ? "<color=lime>활성</color>" : "<color=red>사망</color>")}");

            GUILayout.EndArea();
        }

        private void Label(string text)
        {
            GUILayout.Label(text, _labelStyle);
        }
    }
}
