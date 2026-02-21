using UnityEngine;

namespace Player
{
    /// <summary>
    /// 잔상(Afterimage) 페이드 아웃 처리.
    /// MoleVisualController.CreateAfterimage()에서 런타임 생성된 오브젝트에 부착된다.
    /// </summary>
    public class GhostFade : MonoBehaviour
    {
        private float _lifetime;
        private float _timer;
        private float _startAlpha;
        private SpriteRenderer _sr;

        public void Setup(float lifetime)
        {
            _lifetime = lifetime;
            _timer = lifetime;
            _sr = GetComponent<SpriteRenderer>();
            if (_sr != null) _startAlpha = _sr.color.a;
            Destroy(gameObject, lifetime + 0.1f);
        }

        private void Update()
        {
            if (_sr == null) return;
            
            _timer -= Time.deltaTime;
            float t = Mathf.Clamp01(_timer / _lifetime);
            
            Color c = _sr.color;
            c.a = _startAlpha * t;
            _sr.color = c;

            if (t <= 0f)
            {
                Destroy(gameObject);
            }
        }
    }
}
