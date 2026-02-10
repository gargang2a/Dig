using System.Collections.Generic;
using UnityEngine;
using Core; // To access GameManager

namespace Tunnel
{
    public class TunnelGenerator : MonoBehaviour
    {
        [Header("Settings")]
        public Material TunnelMaterial;
        public int MaxSegmentsPerChunk = 50;

        [Header("State")]
        private List<TunnelChunk> _chunks = new List<TunnelChunk>();
        private TunnelChunk _currentChunk;
        private Vector3 _lastPointPosition;
        private Vector3 _lastLeftPoint;
        private Vector3 _lastRightPoint;
        private bool _isFirstSegment = true;
        private int _currentSegmentCount = 0;

        private void Start()
        {
            CreateNewChunk();
            _lastPointPosition = transform.position;
        }

        private void Update()
        {
            if (GameManager.Instance == null) return;

            float dist = Vector3.Distance(transform.position, _lastPointPosition);
            if (dist >= GameManager.Instance.Settings.SegmentDistance)
            {
                AddTunnelSegment();
            }
        }

        private void AddTunnelSegment()
        {
            // Calculate width based on player scale/score (placeholder logic for now)
            float width = transform.localScale.x * GameManager.Instance.Settings.TunnelWidthMultiplier; 
            
            // Calculate Left/Right points perpendicular to movement
            Vector3 direction = (transform.position - _lastPointPosition).normalized;
            Vector3 perpendicular = new Vector3(-direction.y, direction.x, 0) * (width * 0.5f);

            Vector3 currentLeft = transform.position + perpendicular;
            Vector3 currentRight = transform.position - perpendicular;

            if (!_isFirstSegment)
            {
                _currentChunk.AddSegment(currentLeft, currentRight, _lastLeftPoint, _lastRightPoint);
                _currentSegmentCount++;

                if (_currentSegmentCount >= MaxSegmentsPerChunk)
                {
                    CreateNewChunk();
                }
            }
            else
            {
                _isFirstSegment = false;
            }

            _lastPointPosition = transform.position;
            _lastLeftPoint = currentLeft;
            _lastRightPoint = currentRight;
        }

        private void CreateNewChunk()
        {
            GameObject chunkObj = new GameObject($"TunnelChunk_{_chunks.Count}");
            chunkObj.transform.parent = null; // Detach from player
            chunkObj.transform.position = Vector3.zero;
            chunkObj.transform.rotation = Quaternion.identity;

            TunnelChunk newChunk = chunkObj.AddComponent<TunnelChunk>();
            MeshRenderer mr = chunkObj.GetComponent<MeshRenderer>();
            mr.material = TunnelMaterial;

            // Optional: Collider setup
            // chunkObj.AddComponent<MeshCollider>(); 

            _chunks.Add(newChunk);
            _currentChunk = newChunk;
            _currentSegmentCount = 0;
        }
    }
}
