using UnityEngine;

namespace Core.Data
{
    [CreateAssetMenu(fileName = "GameSettings", menuName = "DigWar/GameSettings")]
    public class GameSettings : ScriptableObject
    {
        [Header("Movement Settings")]
        [Tooltip("Base movement speed of the mole.")]
        public float BaseSpeed = 5f;
        
        [Tooltip("Base rotation speed (degrees per second).")]
        public float BaseTurnSpeed = 180f;
        
        [Tooltip("Speed multiplier when boosting.")]
        public float BoostMultiplier = 1.5f;

        [Header("Progression Settings")]
        [Tooltip("Score required to increase size by 1 unit.")]
        public float ScorePerSizeUnit = 100f;
        
        [Tooltip("Minimum scale of the mole.")]
        public float MinScale = 0.5f;
        
        [Tooltip("Maximum scale of the mole.")]
        public float MaxScale = 3.0f;

        [Header("Tunnel Settings")]
        [Tooltip("Distance between tunnel mesh segments.")]
        public float SegmentDistance = 0.5f;
        
        [Tooltip("Width of the tunnel relative to player size.")]
        public float TunnelWidthMultiplier = 0.8f;
    }
}
