using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// 터널용 울퉁불퉁한 스트립 텍스처를 프로시저럴 생성한다.
/// Perlin Noise로 상하 가장자리를 불규칙하게 만든다.
/// Unity 메뉴: Tools > Generate Tunnel Texture
/// </summary>
public class TunnelTextureGenerator : EditorWindow
{
    private int _width = 256;
    private int _height = 64;
    private float _noiseScale = 15f;
    private float _edgeRoughness = 0.25f; // 테두리 울퉁불퉁 정도 (0~0.5)
    private float _edgeSoftness = 0.3f;   // 테두리 부드러움 (0=날카로움, 1=매우 둥글)
    private Color _fillColor = new Color(0.77f, 0.64f, 0.40f, 1f);
    private string _saveName = "TunnelStrip";

    [MenuItem("Tools/Generate Tunnel Texture")]
    public static void ShowWindow()
    {
        GetWindow<TunnelTextureGenerator>("Tunnel Texture");
    }

    private void OnGUI()
    {
        GUILayout.Label("터널 스트립 텍스처 생성기", EditorStyles.boldLabel);
        GUILayout.Space(10);

        _width = EditorGUILayout.IntField("Width", _width);
        _height = EditorGUILayout.IntField("Height", _height);
        _noiseScale = EditorGUILayout.Slider("Noise Scale", _noiseScale, 3f, 30f);
        _edgeRoughness = EditorGUILayout.Slider("Edge Roughness", _edgeRoughness, 0.05f, 0.45f);
        _edgeSoftness = EditorGUILayout.Slider("Edge Softness", _edgeSoftness, 0.05f, 0.6f);
        _fillColor = EditorGUILayout.ColorField("Fill Color", _fillColor);
        _saveName = EditorGUILayout.TextField("File Name", _saveName);

        GUILayout.Space(10);
        if (GUILayout.Button("Generate!", GUILayout.Height(40)))
            Generate();
    }

    private void Generate()
    {
        var tex = new Texture2D(_width, _height, TextureFormat.RGBA32, false);

        float halfH = _height * 0.5f;
        float fadeZone = _height * _edgeSoftness; // 부드러움 비례 페이드 영역

        for (int x = 0; x < _width; x++)
        {
            // 상단/하단 가장자리에 Perlin Noise 적용
            float noiseTop = Mathf.PerlinNoise(x / _noiseScale, 0f);
            float noiseBot = Mathf.PerlinNoise(x / _noiseScale, 100f);

            // 가장자리 경계 계산 (중심에서 얼마나 벌어지는지)
            float topEdge = halfH + halfH * (1f - _edgeRoughness)
                + halfH * _edgeRoughness * noiseTop;
            float botEdge = halfH - halfH * (1f - _edgeRoughness)
                - halfH * _edgeRoughness * noiseBot;

            for (int y = 0; y < _height; y++)
            {
                float distFromTop = topEdge - y;
                float distFromBot = y - botEdge;

                // fadeZone 내에서 SmoothStep으로 부드러운 알파 전환
                float alphaTop = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(distFromTop / fadeZone));
                float alphaBot = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(distFromBot / fadeZone));
                float alpha = alphaTop * alphaBot;

                if (alpha > 0.001f)
                {
                    // 미세한 색상 노이즈 (자연스러움)
                    float colorNoise = Mathf.PerlinNoise(
                        x / (_noiseScale * 0.5f), y / (_noiseScale * 0.5f));
                    float variation = (colorNoise - 0.5f) * 0.1f;

                    Color c = _fillColor;
                    c.r = Mathf.Clamp01(c.r + variation);
                    c.g = Mathf.Clamp01(c.g + variation);
                    c.b = Mathf.Clamp01(c.b + variation);
                    c.a = alpha;

                    tex.SetPixel(x, y, c);
                }
                else
                {
                    tex.SetPixel(x, y, Color.clear);
                }
            }
        }

        tex.Apply();

        // 저장
        string dir = "Assets/DigWar/02_Sprites";
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string path = $"{dir}/{_saveName}.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        AssetDatabase.Refresh();

        // Import 설정
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.wrapMode = TextureWrapMode.Repeat; // 타일링 가능
            importer.filterMode = FilterMode.Bilinear;
            importer.alphaIsTransparency = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        Debug.Log($"[TunnelTextureGenerator] 저장 완료: {path}");
        Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }
}
