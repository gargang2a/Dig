Shader "DigWar/MoleAnimated"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        
        [Header(Animation Parameters)]
        _Speed ("Speed (0-1)", Range(0, 1)) = 0
        _IsAttacking ("Is Attacking (0 or 1)", Range(0, 1)) = 0
        _HitFlash ("Hit Flash (0-1)", Range(0, 1)) = 0
        
        [Header(Squash and Stretch)]
        _SquashStretchAmount ("Squash Stretch", Range(0, 0.3)) = 0.1
        
        [Header(Breathing)]
        _BreathSpeed ("Breath Speed", Range(0, 10)) = 2.0
        _BreathAmount ("Breath Amount", Range(0, 0.05)) = 0.015
        
        [Header(Drill Visuals)]
        _DrillRegion ("Drill Region Y", Range(0, 1)) = 0.7
        _HazeDistortion ("Haze Distortion", Range(0, 0.05)) = 0.01
        _HazeFrequency ("Haze Frequency", Range(10, 300)) = 150.0
        _BlurStrength ("Blur Strength", Range(0, 0.05)) = 0.02
        _DrillWobbleSpeed ("Wobble Speed", Range(0, 100)) = 30.0
        
        [Header(Attack Glow)]
        _AttackColor ("Attack Glow Color", Color) = (1, 0.6, 0.2, 1)
        _AttackGlowStrength ("Attack Glow Strength", Range(0, 1)) = 0.3
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
            
            struct appdata
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 uv       : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 uv       : TEXCOORD0;
            };
            
            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            
            // Animation Params
            float _Speed;
            float _IsAttacking;
            float _HitFlash;
            float _SquashStretchAmount;
            float _BreathSpeed;
            float _BreathAmount;
            
            // Drill Params
            float _DrillRegion;
            float _HazeDistortion;
            float _HazeFrequency;
            float _BlurStrength;
            float _DrillWobbleSpeed;
            
            // Attack Params
            fixed4 _AttackColor;
            float _AttackGlowStrength;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                
                float2 localPos = v.vertex.xy;
                float time = _Time.y;
                
                // 1. Squash & Stretch
                float stretchFactor = _Speed * _SquashStretchAmount;
                localPos.y *= (1.0 + stretchFactor);
                localPos.x *= (1.0 - stretchFactor * 0.5);
                
                // 2. Breathing
                float breathMask = 1.0 - _Speed;
                float breath = sin(time * _BreathSpeed) * _BreathAmount * breathMask;
                localPos.y += breath;
                localPos.x -= breath * 0.5;
                
                // 3. Heat Haze (Drill)
                float drillMask = smoothstep(_DrillRegion - 0.05, _DrillRegion + 0.05, v.uv.y);
                
                // [Change] 공격 안 해도 항상 30% 정도는 돌고 있음 (땅 파는 중이니까)
                float intensity = max(0.3, max(_Speed, _IsAttacking));
                
                float freq = _HazeFrequency + (intensity * 100.0);
                float amp = _HazeDistortion * (0.5 + intensity * 0.5);
                float heatHaze = sin(v.vertex.y * 20.0 + time * freq) * amp;
                float largeWobble = sin(time * _DrillWobbleSpeed) * 0.01 * intensity;
                
                localPos.x += (heatHaze + largeWobble) * drillMask;
                
                v.vertex.xy = localPos;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _Color;
                
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 4. Motion Blur
                fixed4 col = tex2D(_MainTex, i.uv);
                
                float drillMask = smoothstep(_DrillRegion, _DrillRegion + 0.1, i.uv.y);
                
                // [Change] 항상 30% 이상 회전 (블러도 약하게 들어감)
                float intensity = max(0.3, max(_Speed, _IsAttacking));
                
                if (intensity > 0.1 && drillMask > 0.01)
                {
                    float blurAmount = _BlurStrength * (intensity * 1.5) * drillMask;
                    fixed4 leftCol = tex2D(_MainTex, i.uv + float2(-blurAmount, 0));
                    fixed4 rightCol = tex2D(_MainTex, i.uv + float2(blurAmount, 0));
                    col = col * 0.5 + leftCol * 0.25 + rightCol * 0.25;
                }
                
                col *= i.color;
                
                // 5. Hit Flash
                col.rgb = lerp(col.rgb, fixed3(1, 1, 1), _HitFlash);
                
                // 6. Attack Glow
                float glowMask = _IsAttacking * _AttackGlowStrength;
                col.rgb = lerp(col.rgb, _AttackColor.rgb, glowMask * col.a);
                
                col.rgb *= col.a;
                return col;
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}
