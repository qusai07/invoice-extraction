namespace InvoiceFlow.Evals.Synthetic;

/// <summary>Fictional suppliers and items for synthetic invoices. Any resemblance to real companies is unintended.</summary>
internal static class Catalog
{
    public sealed record Supplier(string En, string Ar, string AddressEn, string AddressAr, string Accent)
    {
        public string Id => "SUP-" + (Array.IndexOf(Suppliers, this) + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture);

        // Stable per supplier: same TRN / IBAN on every invoice, and in the master data.
        private string Index => (Array.IndexOf(Suppliers, this) + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>15 digits: "100" + 2-digit index + 10 digits.</summary>
        public string Trn => "100" + Index + "4567890123";

        /// <summary>23 chars: "AE" + 2 check + 3 bank + 16 account.</summary>
        public string Iban => "AE07" + "033" + Index + "12345678901234";

        /// <summary>Building materials are 3-way matched (goods receipt required).</summary>
        public bool RequiresGoodsReceipt => En.Contains("Building", StringComparison.Ordinal);
    }

    public sealed record Item(string Sku, string En, string Ar, decimal MinPrice, decimal MaxPrice, bool IsService);

    public static readonly Supplier[] Suppliers =
    [
        new("Al Noor Office Supplies LLC", "مؤسسة النور للقرطاسية ذ.م.م", "Office 12, Al Qusais Industrial Area 2, Dubai, UAE", "مكتب 12، القصيص الصناعية 2، دبي، الإمارات", "#1f4e79"),
        new("Gulf Star Trading Co. LLC", "شركة نجمة الخليج للتجارة ذ.م.م", "Warehouse 7, Mussafah M-14, Abu Dhabi, UAE", "مستودع 7، مصفح م-14، أبوظبي، الإمارات", "#8a1c1c"),
        new("Emirates Facility Care LLC", "الإمارات للعناية بالمرافق ذ.م.م", "Floor 3, Al Khalidiya Tower, Abu Dhabi, UAE", "الطابق 3، برج الخالدية، أبوظبي، الإمارات", "#0f6b4f"),
        new("Desert Rose IT Solutions FZE", "وردة الصحراء لحلول تقنية المعلومات م.م.ح", "Unit B-204, SAIF Zone, Sharjah, UAE", "وحدة ب-204، المنطقة الحرة بمطار الشارقة، الإمارات", "#6b2c91"),
        new("Blue Wave Cleaning Services LLC", "الموجة الزرقاء لخدمات التنظيف ذ.م.م", "Shop 4, Al Nahda 1, Dubai, UAE", "محل 4، النهدة 1، دبي، الإمارات", "#00609c"),
        new("Falcon Freight & Logistics FZCO", "الصقر للشحن والخدمات اللوجستية ش.م.ح", "Plot S-30, JAFZA South, Dubai, UAE", "قطعة S-30، جافزا الجنوبية، دبي، الإمارات", "#444444"),
        new("Pearl Coast Catering LLC", "ساحل اللؤلؤ للتموين ذ.م.م", "Building 9, Al Jurf Industrial, Ajman, UAE", "مبنى 9، الجرف الصناعية، عجمان، الإمارات", "#a35a00"),
        new("Oasis Building Materials Trading", "الواحة لتجارة مواد البناء", "Industrial Area 5, Sharjah, UAE", "المنطقة الصناعية 5، الشارقة، الإمارات", "#5a6b1c"),
    ];

    public const string CustomerEn = "Sample Customer Holding LLC";
    public const string CustomerAr = "شركة العميل النموذجي القابضة ذ.م.م";
    public const string CustomerTrn = "100555000111003";

    /// <summary>Another company's TRN, for the "wrong-customer" scenario.</summary>
    public const string OtherCompanyTrn = "100222333444003";

    public static readonly Item[] Items =
    [
        new("PAP-A4-80", "A4 copy paper 80gsm (box of 5 reams)", "ورق تصوير A4 80 غم (كرتونة 5 رزم)", 85, 110, false),
        new("TON-26A", "Laser toner cartridge 26A", "خرطوشة حبر ليزر 26A", 280, 450, false),
        new("CHR-ERG-02", "Ergonomic office chair, mesh back", "كرسي مكتب مريح بظهر شبكي", 450, 1200, false),
        new("LAP-14-I7", "Laptop 14\" i7 16GB 512GB SSD", "حاسوب محمول 14 بوصة i7 ذاكرة 16 جيجا", 3800, 6200, false),
        new("MON-27-4K", "27\" 4K monitor", "شاشة 27 بوصة 4K", 1100, 1900, false),
        new("CBL-CAT6-305", "CAT6 network cable, 305 m box", "كابل شبكة CAT6 بكرة 305 متر", 420, 690, false),
        new("SVC-CLN-MON", "Office cleaning service — monthly", "خدمة تنظيف المكاتب — شهرية", 6500, 14500, true),
        new("SVC-AC-PPM", "AC preventive maintenance visit", "زيارة صيانة وقائية للتكييف", 750, 2400, true),
        new("SVC-CAT-LUN", "Staff lunch catering (per person)", "تموين غداء الموظفين (للشخص)", 28, 55, false),
        new("SVC-FRT-40", "Freight charges 40ft container", "رسوم شحن حاوية 40 قدم", 3200, 7800, true),
        new("CEM-OPC-50", "OPC cement 50 kg bag", "أسمنت بورتلاندي عادي كيس 50 كغ", 14, 22, false),
        new("STL-REB-16", "Steel rebar 16 mm (ton)", "حديد تسليح 16 مم (طن)", 2350, 2900, false),
        new("PNT-EMU-18", "Emulsion paint 18 L, white", "دهان مائي 18 لتر أبيض", 145, 260, false),
        new("WTR-500-24", "Drinking water 500 ml × 24", "مياه شرب 500 مل × 24", 9.5m, 16, false),
        new("SVC-SUP-HR", "On-site IT support (per hour)", "دعم تقني في الموقع (بالساعة)", 180, 350, false),
        new("LIC-M365-BP", "Productivity suite licence, annual (per user)", "ترخيص حزمة إنتاجية سنوي (لكل مستخدم)", 420, 980, false),
    ];

    public static readonly string[] ArabicMonths =
        ["يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر"];
}
