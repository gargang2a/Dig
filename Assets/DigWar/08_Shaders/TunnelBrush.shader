Shader "DigWar/TunnelBrush"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Hardness ("Hardness", Range(0, 1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" } // RT에 그릴 거라 Opaque처럼 취급해도 됨
        LOD 100
        Blend One One  // 누적 블렌딩
        BlendOp Max    // 기존 값과 새 값 중 최대값 선택 (겹치는 브러쉬 문제 해결)
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
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float _Hardness;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 중심(0.5, 0.5)으로부터의 거리
                float2 center = float2(0.5, 0.5);
                float dist = distance(i.uv, center);
                
                // 원형 마스크 (반지름 0.5)
                // Hardness에 따라 가장자리 부드러움 조절
                float alpha = 1.0 - smoothstep(0.5 * _Hardness, 0.5, dist);

                return _Color * alpha;
            }
            ENDCG
        }
    }
}
