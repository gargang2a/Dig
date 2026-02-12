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
        void AddScore(float amount);
    }
}
