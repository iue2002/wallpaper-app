using System.Text.Json.Serialization;

namespace App1.Models
{
    /// <summary>
    /// Peapix API响应模型
    /// </summary>
    public class PeapixWallpaper
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;
        
        [JsonPropertyName("copyright")]
        public string Copyright { get; set; } = string.Empty;
        
        [JsonPropertyName("fullUrl")]
        public string FullUrl { get; set; } = string.Empty;
        
        [JsonPropertyName("thumbUrl")]
        public string ThumbUrl { get; set; } = string.Empty;
        
        [JsonPropertyName("imageUrl")]
        public string ImageUrl { get; set; } = string.Empty;
        
        [JsonPropertyName("pageUrl")]
        public string PageUrl { get; set; } = string.Empty;
    }
}
