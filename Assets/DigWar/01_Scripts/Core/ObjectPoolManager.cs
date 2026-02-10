using System.Collections.Generic;
using UnityEngine;

namespace Core
{
    /// <summary>
    /// 오브젝트 재사용을 위한 풀링 시스템.
    /// 생성(Instantiate)과 파괴(Destroy) 대신 활성/비활성을 전환하여
    /// 가비지 컬렉션(GC) 스파이크를 방지하고 메모리 단편화를 줄인다.
    /// </summary>
    public class ObjectPoolManager : MonoBehaviour
    {
        public static ObjectPoolManager Instance { get; private set; }

        private readonly Dictionary<GameObject, Queue<GameObject>> _pools = new Dictionary<GameObject, Queue<GameObject>>();
        private Transform _poolContainer;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            _poolContainer = new GameObject("PoolContainer").transform;
            _poolContainer.SetParent(transform);
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// 풀에서 오브젝트를 가져온다. 없으면 새로 생성한다.
        /// </summary>
        public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (!_pools.ContainsKey(prefab))
            {
                _pools[prefab] = new Queue<GameObject>();
            }

            GameObject obj;
            if (_pools[prefab].Count > 0)
            {
                obj = _pools[prefab].Dequeue();
            }
            else
            {
                obj = Instantiate(prefab, _poolContainer);
            }

            obj.transform.SetPositionAndRotation(position, rotation);
            obj.SetActive(true);

            // IPoolable 인터페이스 호출
            var poolable = obj.GetComponent<IPoolable>();
            poolable?.OnSpawn();

            return obj;
        }

        /// <summary>
        /// 오브젝트를 풀로 반환한다. (비활성화)
        /// </summary>
        /// <param name="prefab">원래의 프리팹 키 (어느 풀로 보낼지)</param>
        /// <param name="instance">반환할 인스턴스</param>
        public void Despawn(GameObject prefab, GameObject instance)
        {
            if (instance == null) return;

            var poolable = instance.GetComponent<IPoolable>();
            poolable?.OnDespawn();

            instance.SetActive(false);

            if (!_pools.ContainsKey(prefab))
                _pools[prefab] = new Queue<GameObject>();

            _pools[prefab].Enqueue(instance);
        }
    }
}
