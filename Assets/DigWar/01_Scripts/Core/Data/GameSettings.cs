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
        [Header("이동")]
        [Tooltip("기본 이동 속도 (스케일에 비례하여 자동 보정됨)")]
        [Min(0.1f)] public float BaseSpeed = 5f;
        [Tooltip("기본 회전 속도 (도/초). 크기가 커지면 자동으로 느려짐")]
        [Min(1f)]   public float BaseTurnSpeed = 180f;

        [Header("부스트")]
        [Tooltip("부스트 시 속도 배율")]
        [Range(1f, 3f)] public float BoostMultiplier = 1.5f;
        [Tooltip("부스트 시 초당 소모되는 점수")]
        [Min(0f)]       public float BoostScoreCostPerSecond = 7f;

        [Header("성장")]
        [Tooltip("이 점수마다 크기 1단위 성장")]
        [Min(1f)]   public float ScorePerSizeUnit = 130f;
        [Tooltip("최소 크기")]
        [Min(0.1f)] public float MinScale = 0.5f;
        [Tooltip("최대 크기")]
        [Min(0.1f)] public float MaxScale = 3.0f;
        [Tooltip("초기 카메라 줌 (orthographic size)")]
        [Min(1f)]   public float BaseCameraZoom = 3f;
        [Tooltip("크기 1 증가당 카메라 줌아웃량")]
        [Min(0f)]   public float CameraZoomPerScale = 1f;

        [Header("터널")]
        [Tooltip("포인트 간 최소 간격. 작을수록 곡선이 부드럽다")]
        [Min(0.05f)]      public float SegmentDistance = 0.1f;
        [Tooltip("터널 너비 = 플레이어 크기 × 이 값")]
        [Range(0.1f, 2f)] public float TunnelWidthMultiplier = 0.5f;
        [Tooltip("세그먼트당 최대 포인트 수")]
        [Min(10)]         public int MaxSegmentsPerChunk = 100;
        [Tooltip("초기 터널 최대 길이 (월드 유닛)")]
        [Min(0.1f)] public float BaseTunnelLength = 1f;
        [Tooltip("점수 1당 추가되는 터널 길이 (월드 유닛)")]
        [Min(0f)]   public float TunnelLengthPerScore = 0.05f;

        [Header("보석")]
        [Tooltip("보석 하나당 획득 점수")]
        [Min(1f)]   public float GemScore = 8f;
        [Tooltip("보석 생성 간격 (초)")]
        [Min(0.01f)] public float GemSpawnInterval = 0.07f;
        [Tooltip("플레이어 주변 보석 생성 반경")]
        [Min(5f)]   public float GemSpawnRadius = 15f;
        [Tooltip("동시에 존재할 수 있는 최대 보석 수")]
        [Min(10)]   public int MaxGemCount = 2000;
        [Tooltip("보석이 빨려들어가기 시작하는 거리")]
        [Min(0.5f)] public float GemMagnetRadius = 3f;

        [Header("Combat Reward")]
        [Tooltip("Base score reward for a valid kill")]
        [Min(0f)] public float KillRewardScore = 35f;

        [Header("맵")]
        [Tooltip("원형 맵 반경 (월드 유닛)")]
        [Min(10f)]  public float MapRadius = 97.5f;
        [Tooltip("플레이어 초기 스폰 반경 비율 (MapRadius * 비율)")]
        [Range(0.1f, 1f)] public float PlayerSpawnRadiusRatio = 0.6f;
        [Tooltip("봇 스폰 반경 비율 (MapRadius * 비율)")]
        [Range(0.1f, 1f)] public float BotSpawnRadiusRatio = 0.7f;
        [Tooltip("샌드웜 스폰 반경 비율 (MapRadius * 비율)")]
        [Range(0.1f, 1f)] public float SandwormSpawnRadiusRatio = 0.8f;
        [Tooltip("자동 이동(순찰) 타깃 반경 비율 (MapRadius * 비율)")]
        [Range(0.1f, 1f)] public float AutoPatrolRadiusRatio = 0.75f;
        [Tooltip("리스폰 반경 비율 (MapRadius * 비율)")]
        [Range(0.1f, 1f)] public float RespawnRadiusRatio = 0.5f;
        [Tooltip("경계 근처 경고 시작 거리 (안쪽)")]
        [Min(1f)]   public float MapWarningZone = 5f;
        [Tooltip("경계선 원 해상도 (점 개수)")]
        [Range(32, 128)] public int BoundarySegments = 64;
    }
}
