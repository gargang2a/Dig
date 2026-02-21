Shader "DigWar/GroundMasked"
{
    Properties
    {
        [Header(Textures)]
        _MainTex ("Ground Texture", 2D) = "white" {}
        // _FloorTex is Global
        
        [Header(Colors)]
        _GroundColor ("Ground Color", Color) = (1,1,1,1)
        _FloorColor ("Floor Color", Color) = (1, 1, 1, 1)
        _EdgeColor ("Edge/Outline Color", Color) = (0.3, 0.2, 0.1, 1)
        
        [Header(Settings)]
        _Tiling ("Texture Tiling", Float) = 1.0
        _EdgeWidth ("Edge Width", Range(0.001, 0.1)) = 0.02
        _ShadowStrength ("Inner Shadow Strength", Range(0, 1)) = 0.7
        _ShadowWidth ("Inner Shadow Width", Range(0, 1)) = 0.2

        // 전역 마스크 텍스처는 스크립트에서 _TunnelMask로 설정됨
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float2 worldUV : TEXCOORD1;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            sampler2D _FloorTex;
            sampler2D _TunnelMask;
            float4 _MainTex_ST;
            
            float4 _GroundColor;
            float4 _FloorColor;
            float4 _EdgeColor;
            
            float _Tiling;
            float _EdgeWidth;
            float _ShadowStrength;
            float _ShadowWidth;

            // Global Variables from TunnelMaskManager
            float _UseWorldFloor;
            float _FloorTiling;

            // 맵 크기 정보 (스크립트에서 전달)
            float4 _MapSize; // (Width, Height, OffsetX, OffsetY)

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldUV.x = (worldPos.x - _MapSize.z) / _MapSize.x;
                o.worldUV.y = (worldPos.y - _MapSize.w) / _MapSize.y;

                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 1. Mask Sampling
                float mask = tex2D(_TunnelMask, i.worldUV).r;

                // [Boundary Fix]
                float inBounds = step(0.0, i.worldUV.x) * step(i.worldUV.x, 1.0)
                               * step(0.0, i.worldUV.y) * step(i.worldUV.y, 1.0);
                mask *= inBounds;

                // 2. Texture Sampling
                fixed4 groundCol = tex2D(_MainTex, i.uv * _Tiling) * _GroundColor;
                float2 floorUV = (_UseWorldFloor > 0.5) ? (i.worldUV * _FloorTiling) : (i.uv * _Tiling);
                fixed4 floorCol = tex2D(_FloorTex, floorUV) * _FloorColor;

                // 3. Mixing Logic
                float edgeStart = 0.5 - _EdgeWidth;
                float edgeEnd = 0.5 + _EdgeWidth;
                float tunnelFactor = smoothstep(edgeStart, edgeEnd, mask);
                
                float edgeFactor = 1.0 - abs(tunnelFactor * 2.0 - 1.0);
                edgeFactor = smoothstep(0.0, 0.2, edgeFactor);

                // C. Inner Shadow
                float shadowEnd = 0.5 + _ShadowWidth;
                float shadow = (1.0 - smoothstep(0.5, shadowEnd, mask)) * _ShadowStrength;
                shadow *= step(0.5, mask);

                // 4. Final Composition
                fixed4 finalFloor = floorCol * (1.0 - shadow);
                fixed4 finalCol = lerp(groundCol, finalFloor, tunnelFactor);
                finalCol = lerp(finalCol, _EdgeColor, edgeFactor * step(0.1, mask));

                return finalCol;
            }
            ENDCG
        }
    }
}
