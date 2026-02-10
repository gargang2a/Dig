namespace Core
{
    /// <summary>
    /// 풀링 오브젝트의 활성/비활성 시점 콜백.
    /// </summary>
    public interface IPoolable
    {
        void OnSpawn();
        void OnDespawn();
    }
}
