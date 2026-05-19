namespace FTdx101_WebApp.Services
{
    public class AppMemory
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public long FrequencyHz { get; set; }
        public string Mode { get; set; } = "USB";
        public int ClarifierOffsetHz { get; set; }
        public bool RxClarOn { get; set; }
        public bool TxClarOn { get; set; }
        public int SortOrder { get; set; }
    }
}
