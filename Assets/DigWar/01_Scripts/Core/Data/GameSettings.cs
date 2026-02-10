using UnityEngine;

namespace Core.Data
{
    /// <summary>
    /// 게임 밸런싱 파라미터를 모아두는 ScriptableObject.
    /// 런타임 재컴파일 없이 Inspector에서 조정 가능.
    /// </summary>
    [CreateAssetMenu(fileName = "GameSettings", menuName = "DigWar/GameSettings")]
    public class GameSettings : ScriptableObject
    {
        [Header("Movement")]
        [Min(0.1f)] public float BaseSpeed = 5f;
        [Min(1f)]   public float BaseTurnSpeed = 180f;

        [Header("Boost")]
        [Range(1f, 3f)] public float BoostMultiplier = 1.5f;
        [Min(0f)]       public float BoostScoreCostPerSecond = 5f;

        [Header("Progression")]
        [Min(1f)]   public float ScorePerSizeUnit = 100f;
        [Min(0.1f)] public float MinScale = 0.5f;
        [Min(0.1f)] public float MaxScale = 3.0f;
        [Min(1f)]   public float BaseCameraZoom = 3f;
        [Min(0f)]   public float CameraZoomPerScale = 1f;

        [Header("Tunnel")]
        [Tooltip("포인트 간 최소 간격. 작을수록 곡선이 부드럽다.")]
        [Min(0.05f)]      public float SegmentDistance = 0.1f;
        [Range(0.1f, 2f)] public float TunnelWidthMultiplier = 0.5f;
        [Min(10)]         public int MaxSegmentsPerChunk = 100;

        [Header("Gem System")]
        [Min(1f)]   public float GemScore = 10f;
        [Min(0.1f)] public float GemSpawnInterval = 0.5f;
        [Min(5f)]   public float GemSpawnRadius = 15f;
        [Min(10)]   public int MaxGemCount = 100;
        [Min(0.5f)] public float GemMagnetRadius = 3f;
    }
}
