using UnityEngine;
using Core;

namespace Player
{
    public class PlayerController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform _visualRoot;

        private Camera _mainCamera;
        private float _currentSpeed;
        private bool _isBoosting;

        private void Start()
        {
            _mainCamera = Camera.main;
            if (GameManager.Instance != null && GameManager.Instance.Settings != null)
            {
                _currentSpeed = GameManager.Instance.Settings.BaseSpeed;
            }
            else
            {
                Debug.LogError("GameManager or Settings not found!");
                _currentSpeed = 5f; // Fallback
            }
        }

        private void Update()
        {
            if (GameManager.Instance == null || !GameManager.Instance.IsGameActive) return;

            HandleInput();
            Move();
            Rotate();
        }

        private void HandleInput()
        {
            _isBoosting = Input.GetMouseButton(0);
        }

        private void Move()
        {
            var settings = GameManager.Instance.Settings;
            float speed = settings.BaseSpeed;

            if (_isBoosting)
            {
                speed *= settings.BoostMultiplier;
                // TODO: Consume score/energy
            }

            transform.Translate(Vector3.right * (speed * Time.deltaTime)); 
            // Moving Right because usually 2D sprites face Right (0 degrees)
        }

        private void Rotate()
        {
            Vector3 mousePos = _mainCamera.ScreenToWorldPoint(Input.mousePosition);
            mousePos.z = 0;

            Vector3 direction = mousePos - transform.position;
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            
            Quaternion targetRotation = Quaternion.Euler(0, 0, angle);
            
            var settings = GameManager.Instance.Settings;
            // Lerp implementation for smooth rotation
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, 
                targetRotation, 
                settings.BaseTurnSpeed * Time.deltaTime
            );
        }

        private void OnCollisionEnter2D(Collision2D collision)
        {
            // Simple Death Logic
            if (collision.gameObject.GetComponent<Tunnel.TunnelChunk>())
            {
                Debug.Log($"[Player] Died by hitting tunnel: {collision.gameObject.name}");
                // TODO: Trigger Death Sequence
                // GameManager.Instance.EndGame();
            }
        }
    }
}
