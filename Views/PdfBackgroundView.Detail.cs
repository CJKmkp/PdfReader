using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfReader.Views
{
    public partial class PdfBackgroundView
    {
        private readonly Dictionary<int, Image> _detailImages = new();
        internal int DetailTileCount => _detailImages.Count;
        internal void ClearDetailTiles() { DetailLayer.Children.Clear(); _detailImages.Clear(); }
        internal void SetDetailTile(ViewportTile tile, BitmapSource bitmap)
        {
            if (!_detailImages.TryGetValue(tile.Page, out var image))
            {
                image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                _detailImages.Add(tile.Page, image); DetailLayer.Children.Add(image);
            }
            image.Width = tile.Destination.Width; image.Height = tile.Destination.Height;
            Canvas.SetLeft(image, tile.Destination.X); Canvas.SetTop(image, tile.Destination.Y);
            image.Source = bitmap;
        }
    }
}
