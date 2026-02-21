Shader "DigWar/TunnelBrush"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Hardness ("Hardness", Range(0, 1)) = 0.5
        _Roughness ("Edge Roughness", Range(0, 0.15)) = 0.06
        _NoiseScale ("Noise Scale", Range(5, 50)) = 20.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" }
        LOD 100
        Blend One One  // 누적 블렌딩
        BlendOp Max    // 기존 값과 새 값 중 최대값 선택
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float2 worldUV : TEXCOORD1; // Quad의 월드 위치 (노이즈 시드용)
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float _Hardness;
            float _Roughness;
            float _NoiseScale;

            // Simple hash-based noise (RenderTexture에만 찍히므로 성능 부담 미미)
            float hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            float noise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f); // smoothstep

                float a = hash(i);
                float b = hash(i + float2(1.0, 0.0));
                float c = hash(i + float2(0.0, 1.0));
                float d = hash(i + float2(1.0, 1.0));

                return lerp(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                // GL.QUADS에서 vertex 위치가 곧 UV 공간(0~1) 좌표
                // 이를 노이즈 시드로 사용 (같은 위치에 찍으면 같은 패턴)
                o.worldUV = v.vertex.xy;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 중심(0.5, 0.5)으로부터의 거리
                float2 center = float2(0.5, 0.5);
                float2 dir = i.uv - center;
                float dist = length(dir);
                
                // 각도 기반 노이즈 (원 둘레를 따라 울퉁불퉁)
                // atan2로 현재 픽셀의 각도를 구하고, 월드 위치를 시드로 더함
                float angle = atan2(dir.y, dir.x); // -PI ~ PI
                float worldSeed = i.worldUV.x * 73.0 + i.worldUV.y * 137.0; // 위치별 고유 시드
                float n = noise(float2(angle * _NoiseScale, worldSeed));
                
                // 노이즈로 반지름을 울퉁불퉁하게 변형
                float roughRadius = 0.5 + (n - 0.5) * _Roughness;
                
                // 원형 마스크 (변형된 반지름 사용)
                float alpha = 1.0 - smoothstep(roughRadius * _Hardness, roughRadius, dist);

                return _Color * alpha;
            }
            ENDCG
        }
    }
}
