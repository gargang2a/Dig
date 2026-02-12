Shader "DigWar/GroundMasked"
{
    Properties
    {
        [Header(Textures)]
        _MainTex ("Ground Texture", 2D) = "white" {}
        // _FloorTex is Global
        
        [Header(Colors)]
        _GroundColor ("Ground Color", Color) = (1,1,1,1)
        _FloorColor ("Floor Color", Color) = (1, 1, 1, 1) // 텍스처 색상 그대로 사용
        _EdgeColor ("Edge/Outline Color", Color) = (0.3, 0.2, 0.1, 1) // 조금 더 밝게
        
        [Header(Settings)]
        _Tiling ("Texture Tiling", Float) = 1.0
        _EdgeWidth ("Edge Width", Range(0.001, 0.1)) = 0.02
        _ShadowStrength ("Inner Shadow Strength", Range(0, 1)) = 0.5
        _ShadowWidth ("Inner Shadow Width", Range(0, 1)) = 0.1

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
                float2 worldUV : TEXCOORD1; // 월드 좌표 기반 UV (마스크 조회용)
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            sampler2D _FloorTex;
            sampler2D _TunnelMask; // Global Texture
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
                // 텍스처 UV는 로컬 좌표 or 월드 좌표 선택 가능. 여기서는 로컬 UV 사용
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                
                // 월드 좌표 임시 계산 (Object to World)
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                
                // 월드 좌표 -> 마스크 UV (0~1) 변환
                // 예: 맵이 -50~50이면 Width=100.
                // uv.x = (pos.x - minX) / width
                o.worldUV.x = (worldPos.x - _MapSize.z) / _MapSize.x;
                o.worldUV.y = (worldPos.y - _MapSize.w) / _MapSize.y;

                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 1. 마스크 샘플링 (R 채널만 사용)
                float mask = tex2D(_TunnelMask, i.worldUV).r;

                // [Boundary Fix] 맵 범위(0~1) 밖이면 마스크 강제 0 (=땅)
                // Clamp 모드에서 가장자리 픽셀이 무한히 반복되는 현상 방지
                float inBounds = step(0.0, i.worldUV.x) * step(i.worldUV.x, 1.0)
                               * step(0.0, i.worldUV.y) * step(i.worldUV.y, 1.0);
                mask *= inBounds;

                // 2. 텍스처 샘플링
                fixed4 groundCol = tex2D(_MainTex, i.uv * _Tiling) * _GroundColor;

                // [World Space Floor Option]
                // _UseWorldFloor > 0.5 이면 월드 UV(worldUV는 0~1)에 _FloorTiling을 곱해서 사용
                // 아니면 기존 로컬 UV 사용
                float2 floorUV = (_UseWorldFloor > 0.5) ? (i.worldUV * _FloorTiling) : (i.uv * _Tiling);
                
                fixed4 floorCol = tex2D(_FloorTex, floorUV) * _FloorColor;

                // 3. 믹싱 로직
                // Mask 0: 땅, Mask 1: 터널
                
                // A. 외곽선 (Edge)
                // 마스크 값이 0.1 ~ 0.5 사이인 구간을 테두리로 활용
                // 정확한 두께 제어를 위해서는 SDF(Signed Distance Field)가 필요하지만,
                // 여기서는 간단히 그래디언트 구간을 활용
                
                // Edge Threshold
                float edgeStart = 0.5 - _EdgeWidth; // 예: 0.48
                float edgeEnd = 0.5 + _EdgeWidth;   // 예: 0.52
                
                // Smoothstep으로 마스크를 0 or 1로 이분화하되, 중간에 부드러운 구간을 둠
                float tunnelFactor = smoothstep(edgeStart, edgeEnd, mask);
                
                // B. 테두리 그리기
                // tunnelFactor가 변하는 구간(0->1)이 곧 테두리
                // 미분값(fwidth)을 쓰지 않고, mask 값 자체로 판별
                float edgeFactor = 1.0 - abs(tunnelFactor * 2.0 - 1.0); // 0.5일 때 1, 0/1일 때 0
                edgeFactor = smoothstep(0.0, 0.2, edgeFactor); // 좀 더 선명하게

                // C. 내부 그림자 (branchless: O3 Fix)
                // 터널 안쪽(mask > 0.5)이면서 가장자리에 가까운 곳
                float shadowEnd = 0.5 + _ShadowWidth;
                float shadow = (1.0 - smoothstep(0.5, shadowEnd, mask)) * _ShadowStrength;
                shadow *= step(0.5, mask); // mask <= 0.5이면 0

                // 4. 최종 합성
                // Tunnel Floor에 그림자 적용
                fixed4 finalFloor = floorCol * (1.0 - shadow);
                
                // 땅 vs (터널+그림자) 믹스
                fixed4 finalCol = lerp(groundCol, finalFloor, tunnelFactor);

                // 테두리 덮어쓰기 (선택적)
                // 만약 테두리를 진하게 하고 싶다면:
                finalCol = lerp(finalCol, _EdgeColor, edgeFactor * step(0.1, mask));

                return finalCol;
            }
            ENDCG
        }
    }
}
