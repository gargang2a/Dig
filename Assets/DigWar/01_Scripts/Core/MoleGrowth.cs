using UnityEngine;
using Core.Data;

namespace Core
{
    /// <summary>
    /// 두두지(Mole)의 성장 로직을 담당하는 컴포넌트.
    /// 점수에 따라 크기가 로그 스케일로 증가하며, 터널 너비도 함께 조절한다.
    /// 플레이어와 AI 봇 모두에 부착하여 사용한다.
    /// </summary>
    public class MoleGrowth : MonoBehaviour
    {
        [Header("상태")]
        [SerializeField] private float _currentScore;
        [SerializeField] private float _currentScale = 1f;

        [Header("스무딩 설정")]
        [SerializeField, Range(0.01f, 2f)] private float _scaleSmoothTime = 0.5f;

        private GameSettings _settings;
        private Tunnel.TunnelGenerator _tunnelGenerator;
        private float _scaleVelocity;
        private float _targetScale;

        // 외부에서 점수 및 스케일 참조 가능
        public float CurrentScore => _currentScore;
        public float CurrentScale => _currentScale;

        private void Start()
        {
            if (GameManager.Instance == null || GameManager.Instance.Settings == null)
            {
                enabled = false;
                return;
            }

            _settings = GameManager.Instance.Settings;
            _tunnelGenerator = GetComponent<Tunnel.TunnelGenerator>();

            // 초기 스케일 설정
            _targetScale = _settings.MinScale;
            _currentScale = _targetScale;
            transform.localScale = Vector3.one * _currentScale;
        }

        private void Update()
        {
            if (_settings == null) return;

            // 스케일 스무딩
            _currentScale = Mathf.SmoothDamp(
                transform.localScale.x, _targetScale,
                ref _scaleVelocity, _scaleSmoothTime
            );

            // 최소/최대 스케일 클램핑 (안전장치)
            _currentScale = Mathf.Clamp(_currentScale, _settings.MinScale, _settings.MaxScale);

            // 변환 적용
            transform.localScale = Vector3.one * _currentScale;

            // 터널 너비 동기화: A1 Fix에 의해 TunnelGenerator 내부에서 자동 처리됨
            // if (_tunnelGenerator != null) _tunnelGenerator.UpdateWidth(_currentScale);
        }

        /// <summary>
        /// 점수를 설정하고 목표 스케일을 재계산한다.
        /// </summary>
        public void SetScore(float score)
        {
            _currentScore = Mathf.Max(0f, score);
            CalculateTargetScale();
        }

        /// <summary>
        /// 점수를 추가하고 목표 스케일을 재계산한다.
        /// </summary>
        public void AddScore(float amount)
        {
            SetScore(_currentScore + amount);
        }

        private void CalculateTargetScale()
        {
            if (_settings == null) return;

            // 로그 스케일 성장 곡선: log(1 + Score / Unit)
            float growth = Mathf.Log(1f + _currentScore / _settings.ScorePerSizeUnit);
            
            _targetScale = Mathf.Clamp(
                _settings.MinScale + growth,
                _settings.MinScale,
                _settings.MaxScale
            );
        }
    }
}
