using System.Collections.Generic;
using UnityEngine;

namespace Tunnel
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class TunnelChunk : MonoBehaviour
    {
        private Mesh _mesh;
        private List<Vector3> _vertices = new List<Vector3>();
        private List<int> _triangles = new List<int>();
        private List<Vector2> _uvs = new List<Vector2>();

        private PolygonCollider2D _collider;
        
        // We need to store left and right points separately to construct the hull
        private List<Vector2> _leftPoints = new List<Vector2>();
        private List<Vector2> _rightPoints = new List<Vector2>();

        private void Awake()
        {
            _mesh = new Mesh();
            GetComponent<MeshFilter>().mesh = _mesh;
            _collider = gameObject.AddComponent<PolygonCollider2D>();
        }

        public void AddSegment(Vector3 leftPoint, Vector3 rightPoint, Vector3 prevLeft, Vector3 prevRight)
        {
            int startIndex = _vertices.Count;

            // Generate vertices for the new segment quad
            _vertices.Add(prevLeft);
            _vertices.Add(prevRight);
            _vertices.Add(leftPoint);
            _vertices.Add(rightPoint);

            // Keep track of boundary points for the collider
            // Logic: We build the path by going up the Left side and down the Right side
            if (_leftPoints.Count == 0)
            {
                _leftPoints.Add(prevLeft);
                _rightPoints.Add(prevRight);
            }
            _leftPoints.Add(leftPoint);
            _rightPoints.Add(rightPoint);

            // UVs
            _uvs.Add(new Vector2(0, 0));
            _uvs.Add(new Vector2(1, 0));
            _uvs.Add(new Vector2(0, 1));
            _uvs.Add(new Vector2(1, 1));

            // Triangles (Clockwise)
            _triangles.Add(startIndex + 0);
            _triangles.Add(startIndex + 2);
            _triangles.Add(startIndex + 1);

            _triangles.Add(startIndex + 1);
            _triangles.Add(startIndex + 2);
            _triangles.Add(startIndex + 3);

            UpdateMesh();
            UpdateCollider();
        }

        private void UpdateMesh()
        {
            _mesh.SetVertices(_vertices);
            _mesh.SetTriangles(_triangles, 0);
            _mesh.SetUVs(0, _uvs);
            _mesh.RecalculateBounds();
        }

        private void UpdateCollider()
        {
            // Construct the path: All Left points -> All Right points (reversed)
            List<Vector2> path = new List<Vector2>();
            path.AddRange(_leftPoints);
            
            // Add right points in reverse order to close the loop correctly
            for (int i = _rightPoints.Count - 1; i >= 0; i--)
            {
                path.Add(_rightPoints[i]);
            }

            _collider.SetPath(0, path);
        }
    }
}
