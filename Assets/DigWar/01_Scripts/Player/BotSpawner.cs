using UnityEngine;
using Core;
using Core.Data;

namespace Player
{
    /// <summary>
    /// AI 봇을 맵에 스폰한다.
    /// 플레이어 프리팹과 동일한 구조의 오브젝트를 런타임 생성하되,
    /// PlayerController 대신 AIController를 부착한다.
    /// </summary>
    public class BotSpawner : MonoBehaviour
    {
        [Header("봇 설정")]
        [Tooltip("맵에 유지할 봇 수")]
        [SerializeField] private int _botCount = 3;

        [Tooltip("봇 스프라이트 (플레이어와 다른 색상 권장)")]
        [SerializeField] private Sprite _botSprite;

        [Tooltip("봇 터널 머테리얼")]
        [SerializeField] private Material _tunnelMaterial;

        private GameSettings _settings;

        // 봇 색상 팔레트
        private static readonly Color[] BOT_COLORS = new Color[]
        {
            new Color(0.2f, 0.8f, 0.4f),  // 초록
            new Color(0.8f, 0.3f, 0.3f),  // 빨강
            new Color(0.3f, 0.5f, 0.9f),  // 파랑
            new Color(0.9f, 0.7f, 0.1f),  // 노랑
            new Color(0.7f, 0.3f, 0.9f),  // 보라
            new Color(0.9f, 0.5f, 0.2f),  // 주황
        };

        private void Start()
        {
            if (GameManager.Instance == null) return;
            _settings = GameManager.Instance.Settings;

            for (int i = 0; i < _botCount; i++)
                SpawnBot(i);
        }

        private void SpawnBot(int index)
        {
            // 맵 내 랜덤 위치
            float radius = _settings.MapRadius * 0.7f;
            Vector2 randomPos = Random.insideUnitCircle * radius;

            // 봇 오브젝트 생성
            var botObj = new GameObject($"Bot_{index}");
            botObj.transform.position = new Vector3(randomPos.x, randomPos.y, 0f);
            botObj.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            botObj.transform.localScale = Vector3.one * _settings.MinScale;
            botObj.layer = gameObject.layer;

            // Rigidbody2D (AIController/TunnelGenerator 요구)
            var rb = botObj.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;

            // 충돌 감지용 콜라이더
            var col = botObj.AddComponent<CircleCollider2D>();
            col.radius = 0.3f;
            col.isTrigger = true;

            // 비주얼
            var visualObj = new GameObject("Visuals");
            visualObj.transform.SetParent(botObj.transform, false);

            var sr = visualObj.AddComponent<SpriteRenderer>();
            Color botColor = BOT_COLORS[index % BOT_COLORS.Length];

            if (_botSprite != null)
            {
                sr.sprite = _botSprite;
            }
            else
            {
                // 기본 원형 스프라이트
                var playerSr = FindObjectOfType<PlayerController>()
                    ?.GetComponentInChildren<SpriteRenderer>();
                if (playerSr != null)
                    sr.sprite = playerSr.sprite;
            }

            sr.color = botColor;
            sr.sortingOrder = 1;

            // AI 컨트롤러
            botObj.AddComponent<AIController>();

            // 성장 컴포넌트 (점수 기반 크기 조절)
            botObj.AddComponent<Core.MoleGrowth>();

            // 먼지 파티클
            botObj.AddComponent<DiggingParticle>();

            // 터널 생성기
            var tunnel = botObj.AddComponent<Tunnel.TunnelGenerator>();

            // 봇별 터널 색상: 채도를 낮춰 흙 느낌으로
            Color tunnelColor = botColor * 0.6f;
            tunnelColor = Color.Lerp(tunnelColor, new Color(0.45f, 0.3f, 0.18f), 0.5f); // 흙색 혼합
            tunnelColor.a = 1f;
            tunnel.SetTunnelVisuals(_tunnelMaterial, tunnelColor);
        }
    }
}
