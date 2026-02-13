Shader "DigWar/MoleDrill"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ScrollSpeed ("Scroll Speed", Float) = 5.0
        _Distortion ("Distortion Check", Range(0, 1)) = 0.1
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _ScrollSpeed;
            float _Distortion;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.vertex = UnityObjectToClipPos(IN.vertex);
                OUT.texcoord = IN.texcoord;
                OUT.color = IN.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                // UV 스크롤 (X축 이동 = 회전 효과)
                // _ScrollSpeed가 0이면 멈춤, 높으면 빨리 돔
                float2 uv = IN.texcoord;
                
                // 드릴의 둥근 입체감을 위해 X축 왜곡 (가운데는 빠르고 가장자리는 느리게)
                // 단순 평면 스크롤이 아니라 원통형 회전처럼 보이게 함
                float xOffset = _Time.y * _ScrollSpeed;
                
                // 원통형 맵핑 (간단 버전): UV 그대로 이동
                // 만약 드릴 텍스처가 Seamless가 아니라면 wrap 모드가 Repeat여야 함
                uv.x += xOffset;

                fixed4 c = tex2D(_MainTex, uv) * IN.color;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
