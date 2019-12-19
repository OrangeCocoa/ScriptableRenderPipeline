using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition
{
    [System.Serializable]
    public enum CookieAtlasGraphicsFormat
    {
        R11G11B10 = GraphicsFormat.B10G11R11_UFloatPack32,
        R16G16B16A16 = GraphicsFormat.R16G16B16A16_SFloat,
    }

    class LightCookieManager
    {
        HDRenderPipelineAsset m_RenderPipelineAsset = null;

        internal static readonly int s_texSource = Shader.PropertyToID("_SourceTexture");
        internal static readonly int s_sourceMipLevel = Shader.PropertyToID("_SourceMipLevel");
        internal static readonly int s_sourceSize = Shader.PropertyToID("_SourceSize");

        Material m_MaterialFilterAreaLights;
        MaterialPropertyBlock m_MPBFilterAreaLights;

        Material m_CubeToPanoMaterial;

        RenderTexture m_TempRenderTexture0 = null;
        RenderTexture m_TempRenderTexture1 = null;
        
        // Structure for cookies used by directional and spotlights
        PowerOfTwoTextureAtlas m_CookieAtlas;

        // Structure for cookies used by point lights
        TextureCacheCubemap m_CubeCookieTexArray;
        int cookieAtlasLastValidMip;

        public LightCookieManager(HDRenderPipelineAsset hdAsset, int maxCacheSize)
        {
            // Keep track of the render pipeline asset
            m_RenderPipelineAsset = hdAsset;
            var hdResources = HDRenderPipeline.defaultAsset.renderPipelineResources;

            // Create the texture cookie cache that we shall be using for the area lights
            GlobalLightLoopSettings gLightLoopSettings = hdAsset.currentPlatformRenderPipelineSettings.lightLoopSettings;
            int cookieSize = gLightLoopSettings.cookieAreaTextureArraySize;

            // Also make sure to create the engine material that is used for the filtering
            m_MaterialFilterAreaLights = CoreUtils.CreateEngineMaterial(hdResources.shaders.filterAreaLightCookiesPS);

            int cookieCubeSize = gLightLoopSettings.cubeCookieTexArraySize;
            int cookieAtlasSize = (int)gLightLoopSettings.cookieAtlasSize;
            var cookieFormat = (GraphicsFormat)gLightLoopSettings.cookieAtlasFormat;
            cookieAtlasLastValidMip = gLightLoopSettings.cookieAtlasLastValidMip;

            if (PowerOfTwoTextureAtlas.GetApproxCacheSizeInByte(1, cookieAtlasSize, true, cookieFormat) > HDRenderPipeline.k_MaxCacheSize)
                cookieAtlasSize = PowerOfTwoTextureAtlas.GetMaxCacheSizeForWeightInByte(HDRenderPipeline.k_MaxCacheSize, true, cookieFormat);

            m_CookieAtlas = new PowerOfTwoTextureAtlas(cookieAtlasSize, gLightLoopSettings.cookieAtlasLastValidMip, cookieFormat, name: "Cookie Atlas (Punctual Lights)", useMipMap: true);

            m_CubeToPanoMaterial = CoreUtils.CreateEngineMaterial(hdResources.shaders.cubeToPanoPS);

            m_CubeCookieTexArray = new TextureCacheCubemap("Cookie");
            int cookieCubeResolution = (int)gLightLoopSettings.pointCookieSize;
            if (TextureCacheCubemap.GetApproxCacheSizeInByte(cookieCubeSize, cookieCubeResolution, 1) > HDRenderPipeline.k_MaxCacheSize)
                cookieCubeSize = TextureCacheCubemap.GetMaxCacheSizeForWeightInByte(HDRenderPipeline.k_MaxCacheSize, cookieCubeResolution, 1);

            // TODO: hardcoded format RGBA32, we need to move it to GraphicsFormat and then use the cookie format settings for this as well
            m_CubeCookieTexArray.AllocTextureArray(cookieCubeSize, cookieCubeResolution, TextureFormat.RGBA32, true, m_CubeToPanoMaterial);
        }

        internal void NewFrame() => m_CubeCookieTexArray.NewFrame();

        public void Release()
        {
            CoreUtils.Destroy(m_MaterialFilterAreaLights);
            CoreUtils.Destroy(m_CubeToPanoMaterial);

            if(m_TempRenderTexture0 != null)
            {
                m_TempRenderTexture0.Release();
                m_TempRenderTexture0 = null;
            }
            if (m_TempRenderTexture1 != null)
            {
                m_TempRenderTexture1.Release();
                m_TempRenderTexture1 = null;
            }

            if (m_CookieAtlas != null)
            {
                m_CookieAtlas.Release();
                m_CookieAtlas = null;
            }
            if (m_CubeCookieTexArray != null)
            {
                m_CubeCookieTexArray.Release();
                m_CubeCookieTexArray = null;
            }
        }

        Texture FilterAreaLightTexture(CommandBuffer cmd, Texture source)
        {
            if ( m_MaterialFilterAreaLights == null )
            {
                Debug.LogError( "FilterAreaLightTexture has an invalid shader. Can't filter area light cookie." );
                return null;
            }

#if false
            Texture texCache = m_AreaCookieTexArray.GetTexCache();
            int numMipLevels = m_AreaCookieTexArray.GetNumMipLevels();

            if (m_TempRenderTexture0 == null )
            {
                string cacheName = m_AreaCookieTexArray.GetCacheName();
                m_TempRenderTexture0 = new RenderTexture(texCache.width, texCache.height, 1, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB )
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = true,
                    autoGenerateMips = false,
                    name = cacheName + "TempAreaLightRT0"
                };

                // We start by a horizontal gaussian into mip 1 that reduces the width by a factor 2 but keeps the same height
                m_TempRenderTexture1 = new RenderTexture(texCache.width >> 1, texCache.height, 1, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB )
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    useMipMap = true,
                    autoGenerateMips = false,
                    name = cacheName + "TempAreaLightRT1"
                };
            }

            int sourceWidth = texCache.width;
            int sourceHeight = texCache.height;
            int targetWidth = sourceWidth;
            int targetHeight = sourceHeight;
            Vector4 targetSize = new Vector4( targetWidth, targetHeight, 1.0f / targetWidth, 1.0f / targetHeight );

            // Start by copying the source texture to the array slice's mip 0
            {
                cmd.SetGlobalTexture( s_texSource, source );
                cmd.SetGlobalInt( s_sourceMipLevel, 0 );
                cmd.SetRenderTarget( m_TempRenderTexture0, 0);
                cmd.DrawProcedural(Matrix4x4.identity, m_MaterialFilterAreaLights, 0, MeshTopology.Triangles, 3, 1);
            }

            // Then operate on all the remaining mip levels
            Vector4 sourceSize = Vector4.zero;
            for ( int mipIndex=1; mipIndex < numMipLevels; mipIndex++ )
            {
                {   // Perform horizontal blur
                    targetWidth = Mathf.Max(1, targetWidth  >> 1);

                    sourceSize.Set( sourceWidth, sourceHeight, 1.0f / sourceWidth, 1.0f / sourceHeight );
                    targetSize.Set( targetWidth, targetHeight, 1.0f / targetWidth, 1.0f / targetHeight );

                    cmd.SetGlobalTexture( s_texSource, m_TempRenderTexture0 );
                    cmd.SetGlobalInt( s_sourceMipLevel, mipIndex-1 );          // Use previous mip as source
                    cmd.SetGlobalVector( s_sourceSize, sourceSize );
                    cmd.SetRenderTarget( m_TempRenderTexture1, mipIndex-1 );    // Temp texture is already 1 mip lower than source
                    cmd.DrawProcedural(Matrix4x4.identity, m_MaterialFilterAreaLights, 1, MeshTopology.Triangles, 3, 1);
                }

                sourceWidth = targetWidth;

                {   // Perform vertical blur
                    targetHeight = Mathf.Max(1, targetHeight >> 1);

                    sourceSize.Set( sourceWidth, sourceHeight, 1.0f / sourceWidth, 1.0f / sourceHeight );
                    targetSize.Set( targetWidth, targetHeight, 1.0f / targetWidth, 1.0f / targetHeight );

                    cmd.SetGlobalTexture( s_texSource, m_TempRenderTexture1 );
                    cmd.SetGlobalInt( s_sourceMipLevel, mipIndex-1 );
                    cmd.SetGlobalVector( s_sourceSize, sourceSize );
                    cmd.SetRenderTarget( m_TempRenderTexture0, mipIndex);
                    cmd.DrawProcedural(Matrix4x4.identity, m_MaterialFilterAreaLights, 2, MeshTopology.Triangles, 3, 1);
                }

                sourceHeight = targetHeight;
            }
#endif

            return m_TempRenderTexture0;
        }

        public Vector4 Fetch2DCookie(CommandBuffer cmd, Texture cookie)
        {
            Vector4 scaleBias = Vector4.zero;

            // TODO: check if the blitMips actually does something.
            if (!m_CookieAtlas.UpdateTexture(cmd, cookie, ref scaleBias, blitMips: false))
            {
                Debug.LogWarning($"Can't fit {cookie} in the 2D Cookie Texture Atlas. To solve this issue, increase the size of the cookie atlas in the HDRP settings.");
                return Vector4.zero;
            }

            return scaleBias;
        }

        public Vector4 FetchAreaCookie(CommandBuffer cmd, Texture cookie)
        {
            Vector4 scaleBias = Vector4.zero;
            // Update the mips

            if (!m_CookieAtlas.IsCached(out _, cookie) || m_CookieAtlas.NeedsUpdate(cookie))
            {
                Texture filteredAreaLight = FilterAreaLightTexture(cmd, cookie);
                m_CookieAtlas.AllocateTextureWithoutBlit(cookie, cookie.width, cookie.height, ref scaleBias);
                m_CookieAtlas.BlitTexture(cmd, scaleBias, filteredAreaLight, new Vector4(1, 1, 0, 0));
            }

            return scaleBias;
        }

        public int FetchCubeCookie(CommandBuffer cmd, Texture cookie) => m_CubeCookieTexArray.FetchSlice(cmd, cookie);

        public void ResetAllocator() => m_CookieAtlas.ResetAllocator();

        public void ClearAtlasTexture(CommandBuffer cmd) => m_CookieAtlas.ClearTarget(cmd);

        public RTHandle atlasTexture => m_CookieAtlas.AtlasTexture;
        public Texture cubeCache => m_CubeCookieTexArray.GetTexCache();

        public PowerOfTwoTextureAtlas atlas => m_CookieAtlas;
        public TextureCacheCubemap cubeCookieTexArray => m_CubeCookieTexArray;

        public Vector4 GetCookieAtlasSize()
        {
            return new Vector4(
                m_CookieAtlas.AtlasTexture.rt.width,
                m_CookieAtlas.AtlasTexture.rt.height,
                1.0f / m_CookieAtlas.AtlasTexture.rt.width,
                1.0f / m_CookieAtlas.AtlasTexture.rt.height
            );
        }

        public Vector4 GetCookieAtlasDatas()
        {
            float padding = Mathf.Pow(2.0f, m_CookieAtlas.mipPadding) * 2.0f;
            return new Vector4(
                m_CookieAtlas.AtlasTexture.rt.width,
                padding / (float)m_CookieAtlas.AtlasTexture.rt.width,
                cookieAtlasLastValidMip,
                0
            );
        }
    }
}
