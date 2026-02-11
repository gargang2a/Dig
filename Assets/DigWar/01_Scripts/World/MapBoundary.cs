using UnityEngine;
using Core;
using Core.Data;

namespace World
{
    /// <summary>
    /// 원형 맵 경계 감시.
    /// 플레이어가 경계선에 도달하면 사망 처리.
    /// 경계 근처에서는 화면 가장자리에 빨간 비네트 경고.
    /// </summary>
    public class MapBoundary : MonoBehaviour
    {
        private GameSettings _settings;
        private Transform _playerTransform;
        private float _warningAlpha;

        private void Start()
        {
            if (GameManager.Instance == null || GameManager.Instance.Settings == null)
            {
                Debug.LogError("[MapBoundary] GameManager 누락");
                enabled = false;
                return;
            }

            _settings = GameManager.Instance.Settings;
        }

        private void Update()
        {
            if (!GameManager.Instance.IsGameActive) return;

            if (_playerTransform == null)
            {
                var player = FindObjectOfType<Player.PlayerController>();
                if (player != null) _playerTransform = player.transform;
                else return;
            }

            float distance = _playerTransform.position.magnitude; // 원점 기준
            float radius = _settings.MapRadius;

            // 경계 밖 → 사망
            if (distance >= radius)
            {
                GameManager.Instance.KillPlayer();
                return;
            }

            // 경고 구간 (MapWarningZone 안쪽부터 경고)
            float warningStart = radius - _settings.MapWarningZone;
            if (distance > warningStart)
            {
                float t = (distance - warningStart) / _settings.MapWarningZone;
                _warningAlpha = Mathf.Lerp(0f, 0.5f, t);
            }
            else
            {
                _warningAlpha = 0f;
            }
        }

        private void OnGUI()
        {
            if (_warningAlpha <= 0.01f) return;

            // 화면 가장자리에 빨간 비네트 경고
            Color c = new Color(1f, 0f, 0f, _warningAlpha);
            DrawScreenBorder(c, 40f);
        }

        /// <summary>
        /// 화면 가장자리에 반투명 빨간 띠를 그린다.
        /// </summary>
        private void DrawScreenBorder(Color color, float thickness)
        {
            // GUI.DrawTexture용 단색 텍스처 (캐싱)
            if (_borderTex == null)
            {
                _borderTex = new Texture2D(1, 1);
                _borderTex.SetPixel(0, 0, Color.white);
                _borderTex.Apply();
            }

            GUI.color = color;
            float w = Screen.width;
            float h = Screen.height;

            GUI.DrawTexture(new Rect(0, 0, w, thickness), _borderTex);         // 상
            GUI.DrawTexture(new Rect(0, h - thickness, w, thickness), _borderTex); // 하
            GUI.DrawTexture(new Rect(0, 0, thickness, h), _borderTex);             // 좌
            GUI.DrawTexture(new Rect(w - thickness, 0, thickness, h), _borderTex); // 우

            GUI.color = Color.white;
        }

        private Texture2D _borderTex;
    }
}
