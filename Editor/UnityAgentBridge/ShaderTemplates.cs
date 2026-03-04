using System.Text;

namespace UnityAgentBridge
{
    /// <summary>
    /// Templates for generating shader files.
    /// </summary>
    public static class ShaderTemplates
    {
        /// <summary>
        /// Generate an Unlit shader template.
        /// </summary>
        public static string GenerateUnlitShader(string shaderName, bool hasTexture = true, bool hasColor = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Shader \"{shaderName}\"");
            sb.AppendLine("{");
            sb.AppendLine("    Properties");
            sb.AppendLine("    {");
            if (hasColor)
                sb.AppendLine("        _Color (\"Color\", Color) = (1,1,1,1)");
            if (hasTexture)
                sb.AppendLine("        _MainTex (\"Texture\", 2D) = \"white\" {}");
            sb.AppendLine("    }");
            sb.AppendLine("    SubShader");
            sb.AppendLine("    {");
            sb.AppendLine("        Tags { \"RenderType\"=\"Opaque\" }");
            sb.AppendLine("        LOD 100");
            sb.AppendLine();
            sb.AppendLine("        Pass");
            sb.AppendLine("        {");
            sb.AppendLine("            CGPROGRAM");
            sb.AppendLine("            #pragma vertex vert");
            sb.AppendLine("            #pragma fragment frag");
            sb.AppendLine("            #pragma multi_compile_fog");
            sb.AppendLine();
            sb.AppendLine("            #include \"UnityCG.cginc\"");
            sb.AppendLine();
            sb.AppendLine("            struct appdata");
            sb.AppendLine("            {");
            sb.AppendLine("                float4 vertex : POSITION;");
            if (hasTexture)
                sb.AppendLine("                float2 uv : TEXCOORD0;");
            sb.AppendLine("            };");
            sb.AppendLine();
            sb.AppendLine("            struct v2f");
            sb.AppendLine("            {");
            if (hasTexture)
                sb.AppendLine("                float2 uv : TEXCOORD0;");
            sb.AppendLine("                UNITY_FOG_COORDS(1)");
            sb.AppendLine("                float4 vertex : SV_POSITION;");
            sb.AppendLine("            };");
            sb.AppendLine();
            if (hasTexture)
            {
                sb.AppendLine("            sampler2D _MainTex;");
                sb.AppendLine("            float4 _MainTex_ST;");
            }
            if (hasColor)
                sb.AppendLine("            fixed4 _Color;");
            sb.AppendLine();
            sb.AppendLine("            v2f vert (appdata v)");
            sb.AppendLine("            {");
            sb.AppendLine("                v2f o;");
            sb.AppendLine("                o.vertex = UnityObjectToClipPos(v.vertex);");
            if (hasTexture)
                sb.AppendLine("                o.uv = TRANSFORM_TEX(v.uv, _MainTex);");
            sb.AppendLine("                UNITY_TRANSFER_FOG(o,o.vertex);");
            sb.AppendLine("                return o;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            fixed4 frag (v2f i) : SV_Target");
            sb.AppendLine("            {");
            if (hasTexture && hasColor)
                sb.AppendLine("                fixed4 col = tex2D(_MainTex, i.uv) * _Color;");
            else if (hasTexture)
                sb.AppendLine("                fixed4 col = tex2D(_MainTex, i.uv);");
            else if (hasColor)
                sb.AppendLine("                fixed4 col = _Color;");
            else
                sb.AppendLine("                fixed4 col = fixed4(1, 1, 1, 1);");
            sb.AppendLine("                UNITY_APPLY_FOG(i.fogCoord, col);");
            sb.AppendLine("                return col;");
            sb.AppendLine("            }");
            sb.AppendLine("            ENDCG");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generate a Surface shader template.
        /// </summary>
        public static string GenerateSurfaceShader(string shaderName, bool hasNormalMap = false, bool hasEmission = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Shader \"{shaderName}\"");
            sb.AppendLine("{");
            sb.AppendLine("    Properties");
            sb.AppendLine("    {");
            sb.AppendLine("        _Color (\"Color\", Color) = (1,1,1,1)");
            sb.AppendLine("        _MainTex (\"Albedo (RGB)\", 2D) = \"white\" {}");
            sb.AppendLine("        _Glossiness (\"Smoothness\", Range(0,1)) = 0.5");
            sb.AppendLine("        _Metallic (\"Metallic\", Range(0,1)) = 0.0");
            if (hasNormalMap)
            {
                sb.AppendLine("        _BumpMap (\"Normal Map\", 2D) = \"bump\" {}");
                sb.AppendLine("        _BumpScale (\"Normal Scale\", Float) = 1.0");
            }
            if (hasEmission)
            {
                sb.AppendLine("        [HDR] _EmissionColor (\"Emission Color\", Color) = (0,0,0,1)");
                sb.AppendLine("        _EmissionMap (\"Emission Map\", 2D) = \"black\" {}");
            }
            sb.AppendLine("    }");
            sb.AppendLine("    SubShader");
            sb.AppendLine("    {");
            sb.AppendLine("        Tags { \"RenderType\"=\"Opaque\" }");
            sb.AppendLine("        LOD 200");
            sb.AppendLine();
            sb.AppendLine("        CGPROGRAM");
            sb.AppendLine("        #pragma surface surf Standard fullforwardshadows");
            sb.AppendLine("        #pragma target 3.0");
            sb.AppendLine();
            sb.AppendLine("        sampler2D _MainTex;");
            if (hasNormalMap)
                sb.AppendLine("        sampler2D _BumpMap;");
            if (hasEmission)
                sb.AppendLine("        sampler2D _EmissionMap;");
            sb.AppendLine();
            sb.AppendLine("        struct Input");
            sb.AppendLine("        {");
            sb.AppendLine("            float2 uv_MainTex;");
            if (hasNormalMap)
                sb.AppendLine("            float2 uv_BumpMap;");
            if (hasEmission)
                sb.AppendLine("            float2 uv_EmissionMap;");
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        half _Glossiness;");
            sb.AppendLine("        half _Metallic;");
            sb.AppendLine("        fixed4 _Color;");
            if (hasNormalMap)
                sb.AppendLine("        float _BumpScale;");
            if (hasEmission)
                sb.AppendLine("        fixed4 _EmissionColor;");
            sb.AppendLine();
            sb.AppendLine("        void surf (Input IN, inout SurfaceOutputStandard o)");
            sb.AppendLine("        {");
            sb.AppendLine("            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;");
            sb.AppendLine("            o.Albedo = c.rgb;");
            sb.AppendLine("            o.Metallic = _Metallic;");
            sb.AppendLine("            o.Smoothness = _Glossiness;");
            sb.AppendLine("            o.Alpha = c.a;");
            if (hasNormalMap)
                sb.AppendLine("            o.Normal = UnpackScaleNormal(tex2D(_BumpMap, IN.uv_BumpMap), _BumpScale);");
            if (hasEmission)
                sb.AppendLine("            o.Emission = tex2D(_EmissionMap, IN.uv_EmissionMap).rgb * _EmissionColor.rgb;");
            sb.AppendLine("        }");
            sb.AppendLine("        ENDCG");
            sb.AppendLine("    }");
            sb.AppendLine("    FallBack \"Diffuse\"");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generate a URP Lit shader template.
        /// </summary>
        public static string GenerateURPShader(string shaderName, bool isTransparent = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Shader \"{shaderName}\"");
            sb.AppendLine("{");
            sb.AppendLine("    Properties");
            sb.AppendLine("    {");
            sb.AppendLine("        [MainTexture] _BaseMap (\"Base Map\", 2D) = \"white\" {}");
            sb.AppendLine("        [MainColor] _BaseColor (\"Base Color\", Color) = (1,1,1,1)");
            sb.AppendLine("        _Smoothness (\"Smoothness\", Range(0,1)) = 0.5");
            sb.AppendLine("        _Metallic (\"Metallic\", Range(0,1)) = 0.0");
            if (isTransparent)
            {
                sb.AppendLine("        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend (\"Src Blend\", Float) = 1");
                sb.AppendLine("        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend (\"Dst Blend\", Float) = 0");
                sb.AppendLine("        [Enum(Off, 0, On, 1)] _ZWrite (\"ZWrite\", Float) = 1");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    SubShader");
            sb.AppendLine("    {");
            if (isTransparent)
                sb.AppendLine("        Tags { \"RenderType\"=\"Transparent\" \"Queue\"=\"Transparent\" \"RenderPipeline\"=\"UniversalPipeline\" }");
            else
                sb.AppendLine("        Tags { \"RenderType\"=\"Opaque\" \"RenderPipeline\"=\"UniversalPipeline\" }");
            sb.AppendLine();
            sb.AppendLine("        Pass");
            sb.AppendLine("        {");
            sb.AppendLine("            Name \"ForwardLit\"");
            sb.AppendLine("            Tags { \"LightMode\"=\"UniversalForward\" }");
            sb.AppendLine();
            if (isTransparent)
            {
                sb.AppendLine("            Blend [_SrcBlend] [_DstBlend]");
                sb.AppendLine("            ZWrite [_ZWrite]");
            }
            sb.AppendLine();
            sb.AppendLine("            HLSLPROGRAM");
            sb.AppendLine("            #pragma vertex vert");
            sb.AppendLine("            #pragma fragment frag");
            sb.AppendLine("            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE");
            sb.AppendLine("            #pragma multi_compile _ _ADDITIONAL_LIGHTS");
            sb.AppendLine("            #pragma multi_compile_fragment _ _SHADOWS_SOFT");
            sb.AppendLine();
            sb.AppendLine("            #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl\"");
            sb.AppendLine("            #include \"Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl\"");
            sb.AppendLine();
            sb.AppendLine("            TEXTURE2D(_BaseMap);");
            sb.AppendLine("            SAMPLER(sampler_BaseMap);");
            sb.AppendLine();
            sb.AppendLine("            CBUFFER_START(UnityPerMaterial)");
            sb.AppendLine("                float4 _BaseMap_ST;");
            sb.AppendLine("                half4 _BaseColor;");
            sb.AppendLine("                half _Smoothness;");
            sb.AppendLine("                half _Metallic;");
            sb.AppendLine("            CBUFFER_END");
            sb.AppendLine();
            sb.AppendLine("            struct Attributes");
            sb.AppendLine("            {");
            sb.AppendLine("                float4 positionOS : POSITION;");
            sb.AppendLine("                float3 normalOS : NORMAL;");
            sb.AppendLine("                float2 uv : TEXCOORD0;");
            sb.AppendLine("            };");
            sb.AppendLine();
            sb.AppendLine("            struct Varyings");
            sb.AppendLine("            {");
            sb.AppendLine("                float4 positionCS : SV_POSITION;");
            sb.AppendLine("                float2 uv : TEXCOORD0;");
            sb.AppendLine("                float3 normalWS : TEXCOORD1;");
            sb.AppendLine("                float3 positionWS : TEXCOORD2;");
            sb.AppendLine("            };");
            sb.AppendLine();
            sb.AppendLine("            Varyings vert(Attributes IN)");
            sb.AppendLine("            {");
            sb.AppendLine("                Varyings OUT;");
            sb.AppendLine("                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);");
            sb.AppendLine("                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);");
            sb.AppendLine("                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);");
            sb.AppendLine("                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);");
            sb.AppendLine("                return OUT;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            half4 frag(Varyings IN) : SV_Target");
            sb.AppendLine("            {");
            sb.AppendLine("                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);");
            sb.AppendLine("                half4 color = baseMap * _BaseColor;");
            sb.AppendLine();
            sb.AppendLine("                // Simple lighting");
            sb.AppendLine("                Light mainLight = GetMainLight();");
            sb.AppendLine("                half3 diffuse = LightingLambert(mainLight.color, mainLight.direction, IN.normalWS);");
            sb.AppendLine("                color.rgb *= diffuse + unity_AmbientSky.rgb;");
            sb.AppendLine();
            sb.AppendLine("                return color;");
            sb.AppendLine("            }");
            sb.AppendLine("            ENDHLSL");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // Shadow caster pass");
            sb.AppendLine("        Pass");
            sb.AppendLine("        {");
            sb.AppendLine("            Name \"ShadowCaster\"");
            sb.AppendLine("            Tags { \"LightMode\"=\"ShadowCaster\" }");
            sb.AppendLine();
            sb.AppendLine("            ZWrite On");
            sb.AppendLine("            ZTest LEqual");
            sb.AppendLine("            ColorMask 0");
            sb.AppendLine();
            sb.AppendLine("            HLSLPROGRAM");
            sb.AppendLine("            #pragma vertex ShadowPassVertex");
            sb.AppendLine("            #pragma fragment ShadowPassFragment");
            sb.AppendLine("            #include \"Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl\"");
            sb.AppendLine("            ENDHLSL");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generate a simple Unlit Transparent shader.
        /// </summary>
        public static string GenerateUnlitTransparentShader(string shaderName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Shader \"{shaderName}\"");
            sb.AppendLine("{");
            sb.AppendLine("    Properties");
            sb.AppendLine("    {");
            sb.AppendLine("        _Color (\"Color\", Color) = (1,1,1,1)");
            sb.AppendLine("        _MainTex (\"Texture\", 2D) = \"white\" {}");
            sb.AppendLine("    }");
            sb.AppendLine("    SubShader");
            sb.AppendLine("    {");
            sb.AppendLine("        Tags { \"RenderType\"=\"Transparent\" \"Queue\"=\"Transparent\" }");
            sb.AppendLine("        LOD 100");
            sb.AppendLine();
            sb.AppendLine("        Blend SrcAlpha OneMinusSrcAlpha");
            sb.AppendLine("        ZWrite Off");
            sb.AppendLine();
            sb.AppendLine("        Pass");
            sb.AppendLine("        {");
            sb.AppendLine("            CGPROGRAM");
            sb.AppendLine("            #pragma vertex vert");
            sb.AppendLine("            #pragma fragment frag");
            sb.AppendLine();
            sb.AppendLine("            #include \"UnityCG.cginc\"");
            sb.AppendLine();
            sb.AppendLine("            struct appdata");
            sb.AppendLine("            {");
            sb.AppendLine("                float4 vertex : POSITION;");
            sb.AppendLine("                float2 uv : TEXCOORD0;");
            sb.AppendLine("            };");
            sb.AppendLine();
            sb.AppendLine("            struct v2f");
            sb.AppendLine("            {");
            sb.AppendLine("                float2 uv : TEXCOORD0;");
            sb.AppendLine("                float4 vertex : SV_POSITION;");
            sb.AppendLine("            };");
            sb.AppendLine();
            sb.AppendLine("            sampler2D _MainTex;");
            sb.AppendLine("            float4 _MainTex_ST;");
            sb.AppendLine("            fixed4 _Color;");
            sb.AppendLine();
            sb.AppendLine("            v2f vert (appdata v)");
            sb.AppendLine("            {");
            sb.AppendLine("                v2f o;");
            sb.AppendLine("                o.vertex = UnityObjectToClipPos(v.vertex);");
            sb.AppendLine("                o.uv = TRANSFORM_TEX(v.uv, _MainTex);");
            sb.AppendLine("                return o;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            fixed4 frag (v2f i) : SV_Target");
            sb.AppendLine("            {");
            sb.AppendLine("                fixed4 col = tex2D(_MainTex, i.uv) * _Color;");
            sb.AppendLine("                return col;");
            sb.AppendLine("            }");
            sb.AppendLine("            ENDCG");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generate a shader template based on type.
        /// </summary>
        public static string GenerateShaderTemplate(string shaderType, string shaderName)
        {
            return shaderType?.ToLowerInvariant() switch
            {
                "unlit" => GenerateUnlitShader(shaderName),
                "unlittransparent" or "transparent" => GenerateUnlitTransparentShader(shaderName),
                "surface" => GenerateSurfaceShader(shaderName),
                "surfacenormal" => GenerateSurfaceShader(shaderName, hasNormalMap: true),
                "surfaceemission" => GenerateSurfaceShader(shaderName, hasEmission: true),
                "surfacefull" => GenerateSurfaceShader(shaderName, hasNormalMap: true, hasEmission: true),
                "urp" => GenerateURPShader(shaderName),
                "urptransparent" => GenerateURPShader(shaderName, isTransparent: true),
                _ => GenerateUnlitShader(shaderName)
            };
        }
    }
}
