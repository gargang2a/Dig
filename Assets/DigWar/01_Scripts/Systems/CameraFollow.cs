using UnityEngine;

namespace Systems
{
    /// <summary>
    /// 카메라가 타겟(플레이어)을 추적한다.
    /// LateUpdate에서 실행되어 이동 연산이 끝난 뒤 위치를 갱신한다.
    /// </summary>
    public class CameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform _target;

        [Tooltip("0이면 즉시, 높을수록 부드럽게 추적")]
        [SerializeField, Range(0f, 0.5f)] private float _smoothTime = 0.05f;

        private Vector3 _velocity;

        private void LateUpdate()
        {
            if (_target == null) return;

            Vector3 dest = _target.position;
            dest.z = transform.position.z;

            if (_smoothTime <= 0.001f)
            {
                transform.position = dest;
            }
            else
            {
                transform.position = Vector3.SmoothDamp(
                    transform.position, dest,
                    ref _velocity, _smoothTime
                );
            }
        }

        /// <summary>
        /// 타겟 위치로 즉시 점프한다. 씬 시작 시 초기 위치 설정용.
        /// </summary>
        public void SnapToTarget()
        {
            if (_target == null) return;
            Vector3 pos = _target.position;
            pos.z = transform.position.z;
            transform.position = pos;
            _velocity = Vector3.zero;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_target != null) return;
            var player = FindObjectOfType<Player.PlayerController>();
            if (player != null)
                _target = player.transform;
        }
#endif
    }
}
