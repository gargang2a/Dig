Shader "DigWar/TunnelEraser"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Hardness ("Hardness", Range(0, 1)) = 0.7
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" }
        LOD 100
        // 표준 알파 블렌딩 (가장 안정적)
        // result = src.rgb * src.a + dest.rgb * (1 - src.a)
        // src = (0,0,0, alpha) → result = dest * (1 - alpha)
        // alpha=1 → dest * 0 = 0 (지워짐)
        // alpha=0 → dest * 1 = dest (유지)
        Blend SrcAlpha OneMinusSrcAlpha
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
                float2 center = float2(0.5, 0.5);
                float dist = length(i.uv - center);

                // 원형 감쇠: 중심=1, 가장자리=0
                float mask = 1.0 - smoothstep(_Hardness * 0.5, 0.5, dist);

                // RGB=0(검정), Alpha=mask
                // 블렌딩: dest * (1 - mask)
                // mask=1 → dest * 0 = 흙으로 복구
                // mask=0 → dest * 1 = 기존 터널 유지
                return fixed4(0, 0, 0, mask);
            }
            ENDCG
        }
    }
}
