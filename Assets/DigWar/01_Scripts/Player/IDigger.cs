namespace Player
{
    /// <summary>
    /// PlayerController와 AIController의 공통 인터페이스.
    /// TunnelGenerator가 이 인터페이스를 통해 속도/부스트 정보를 얻는다.
    /// </summary>
    public interface IDigger
    {
        float CurrentSpeed { get; }
        bool IsBoosting { get; }
        
        /// <summary>공격 모드(Assault) 여부. 터널 생성 + 처치 가능 상태.</summary>
        bool IsAttacking { get; }
        
        void AddScore(float amount);
        
        /// <summary>사망 처리.</summary>
        void Die();
    }
}
