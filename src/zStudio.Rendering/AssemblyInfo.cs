// Temporary during the renderer migration (P1): the facade types for partials still importing backend namespaces.
global using RenderView = zStudio.Rendering.Direct3D.RenderView;
global using RenderCamera = zStudio.Rendering.Direct3D.RenderCamera;
global using PerspectiveRenderCamera = zStudio.Rendering.Direct3D.PerspectiveRenderCamera;
global using OrthographicRenderCamera = zStudio.Rendering.Direct3D.OrthographicRenderCamera;
global using RenderDevice = zStudio.Rendering.Direct3D.RenderDevice;
global using RenderTexture = zStudio.Rendering.Direct3D.RenderTexture;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("zStudio.Desktop.Tests")]
[assembly: InternalsVisibleTo("zStudio.PreviewCheck")]
