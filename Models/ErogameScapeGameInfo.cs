using System;
using System.Collections.Generic;

namespace ErogameScapeMetadata.Models
{
    public class ErogameScapeGameInfo
    {
        private const string ErogameScapeGameUrlTemplate =
            "https://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/game.php?game={0}";

        private const string DmmCoverUrlTemplate =
            "https://pics.dmm.co.jp/digital/pcgame/{0}/{0}pl.jpg";

        public int Id { get; set; }
        public string GameName { get; set; }
        public string Furigana { get; set; }
        public string BrandName { get; set; }
        public string BrandUrl { get; set; }
        public DateTime? SellDay { get; set; }
        public int? Median { get; set; }
        public int? Average { get; set; }
        public int? ReviewCount { get; set; }
        public string DmmId { get; set; }
        public string DmmSubsc { get; set; }
        public string OfficialGenre { get; set; }
        public string Shoukai { get; set; }
        public string DlsiteId { get; set; }
        public string DlsiteDomain { get; set; }
        public string Description { get; set; }
        public bool IsEroge { get; set; }
        public string SeriesName { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> Genres { get; set; } = new List<string>();
        public List<string> Features { get; set; } = new List<string>();
        public List<string> BackgroundImageUrls { get; set; } = new List<string>();
        public string VndbCoverImageUrl { get; set; }
        public bool VndbCoverIsPortrait { get; set; }

        public string GetCoverImageUrl()
        {
            var dmmUrl = GetDmmCoverUrl();

            // VNDBカバーが縦長なら最優先
            if (!string.IsNullOrEmpty(VndbCoverImageUrl) && VndbCoverIsPortrait)
                return VndbCoverImageUrl;

            // DMMパッケージ画像があればそちらを試す
            if (dmmUrl != null)
                return dmmUrl;

            // VNDBカバー（横長でも他に選択肢がない場合）
            if (!string.IsNullOrEmpty(VndbCoverImageUrl))
                return VndbCoverImageUrl;

            return null;
        }

        private string GetDmmCoverUrl()
        {
            var dmm = DmmSubsc ?? DmmId;
            if (string.IsNullOrEmpty(dmm))
                return null;
            return string.Format(DmmCoverUrlTemplate, dmm);
        }

        public string GetErogameScapeUrl()
        {
            return string.Format(ErogameScapeGameUrlTemplate, Id);
        }
    }
}
