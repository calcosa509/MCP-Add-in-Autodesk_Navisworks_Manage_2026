using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using waabe_navi_mcp_server.Contracts;
namespace waabe_navi_mcp_server.Services.Backends
{
    public sealed partial class FallbackBackend
    {
        public async Task<ExportViewDto> ExportCurrentViewAsync(int width, int height, CancellationToken ct)
        {
            int w = width > 0 ? width : 1280;
            int h = height > 0 ? height : 720;
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wv_" + System.DateTime.Now.Ticks + ".png");
            await Task.Run(() => {
                var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                using (var s = new Bitmap(b.Width, b.Height))
                using (var g = Graphics.FromImage(s))
                { g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
                  using (var r = new Bitmap(s, new System.Drawing.Size(w, h)))
                    r.Save(path, ImageFormat.Png); }
            });
            var bytes = System.IO.File.ReadAllBytes(path);
            var b64 = System.Convert.ToBase64String(bytes);
            System.IO.File.Delete(path);
            return new ExportViewDto { file_path = path, base64_image = b64, width = w, height = h, success = true, message = "ok" };
        }
    }
}