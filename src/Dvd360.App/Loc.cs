using System.ComponentModel;
using System.Globalization;

namespace Dvd360.App;

/// <summary>Tiny two-language string table. XAML binds to <c>{Binding [Key], Source={x:Static local:Loc.I}}</c>.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool English { get; private set; } = !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("tr", StringComparison.OrdinalIgnoreCase);

    public string this[string key] => T(key);

    public static string T(string key, params object[] args)
    {
        if (!Strings.TryGetValue(key, out var pair)) return key;
        string s = I.English ? pair.En : pair.Tr;
        return args.Length > 0 ? string.Format(s, args) : s;
    }

    public void Toggle()
    {
        English = !English;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    private static readonly Dictionary<string, (string Tr, string En)> Strings = new()
    {
        ["AppSubtitle"] = ("Çizik CD / DVD kurtarma", "Scratched CD / DVD rescue"),
        ["Lang"] = ("EN", "TR"),
        ["Drive"] = ("Sürücü", "Drive"),
        ["Refresh"] = ("Yenile", "Refresh"),
        ["NoDrive"] = ("CD/DVD sürücüsü bulunamadı", "No CD/DVD drive found"),
        ["InsertDisc"] = ("Disk takın", "Insert a disc"),
        ["Detecting"] = ("Disk algılanıyor...", "Detecting disc..."),
        ["DiscDetected"] = ("Disk algılandı", "Disc detected"),
        ["DiscRemoved"] = ("Disk çıkarıldı", "Disc removed"),
        ["Media"] = ("Tür", "Type"),
        ["Label"] = ("Etiket", "Label"),
        ["Size"] = ("Boyut", "Size"),
        ["Tracks"] = ("İzler", "Tracks"),
        ["DriveModel"] = ("Sürücü", "Drive"),

        ["TabRescue"] = ("Kurtarma", "Rescue"),
        ["TabContent"] = ("İçerik", "Contents"),
        ["TabVideo"] = ("Video → MP4", "Video → MP4"),
        ["TabHelp"] = ("Yardım", "Help"),

        ["SaveTo"] = ("Kayıt yeri", "Save to"),
        ["Choose"] = ("Seç...", "Choose..."),
        ["ImageName"] = ("Kalıp adı", "Image name"),
        ["FreeSpace"] = ("Boş alan: {0}  •  Gerekli: {1}", "Free: {0}  •  Needed: {1}"),
        ["NotEnoughSpace"] = ("Seçilen yerde yeterli boş alan yok.", "Not enough free space in the chosen place."),
        ["Retries"] = ("Bozuk sektör deneme sayısı", "Tries per bad sector"),
        ["Speed"] = ("Okuma hızı", "Read speed"),
        ["SpeedMax"] = ("En yüksek", "Maximum"),
        ["SpeedHint"] = ("Çizik disklerde düşük hız daha iyi okur", "Slow speeds read scratched discs better"),
        ["Verify"] = ("Her sektörü kendim doğrula (EDC)", "Verify every sector myself (EDC)"),
        ["Start"] = ("Kurtarmayı başlat", "Start rescue"),
        ["Continue"] = ("Devam et", "Continue"),
        ["Stop"] = ("Durdur", "Stop"),
        ["RetryBad"] = ("Bozukları tekrar dene", "Retry bad sectors"),
        ["OpenFolder"] = ("Klasörü aç", "Open folder"),
        ["StatOk"] = ("Okunan", "Read"),
        ["StatBad"] = ("Bozuk", "Bad"),
        ["StatPending"] = ("Bekleyen", "Pending"),
        ["StatSpeed"] = ("Hız", "Speed"),
        ["StatElapsed"] = ("Süre", "Elapsed"),
        ["StatEta"] = ("Kalan", "Remaining"),
        ["Legend"] = ("■ okundu   ■ bozuk   ■ tekrar denenecek   ■ bekliyor", "■ read   ■ bad   ■ to retry   ■ waiting"),
        ["LegendOk"] = ("okundu", "read"),
        ["LegendBad"] = ("bozuk", "bad"),
        ["LegendRetry"] = ("tekrar denenecek", "to retry"),
        ["LegendWait"] = ("bekliyor", "waiting"),
        ["Pass_Idle"] = ("Hazır", "Ready"),
        ["Pass_FastCopy"] = ("1. geçiş: hızlı okuma", "Pass 1: fast read"),
        ["Pass_Gaps"] = ("2. geçiş: atlanan bölgeler", "Pass 2: areas jumped over"),
        ["Pass_SectorBySector"] = ("3. geçiş: sektör sektör", "Pass 3: sector by sector"),
        ["Pass_RetryBad"] = ("Bozuk sektörler yeniden deneniyor", "Retrying bad sectors"),
        ["Pass_Done"] = ("Tamamlandı", "Finished"),
        ["Resuming"] = ("Önceki kurtarma bulundu, kaldığı yerden devam ediliyor.", "Found a previous rescue, continuing where it stopped."),
        ["DoneAllGood"] = ("Kurtarma tamamlandı — tüm sektörler okundu!", "Rescue finished — every sector was read!"),
        ["DoneSomeBad"] = ("Kurtarma bitti. {0} sektör okunamadı ({1}). Diski temizleyip \"Bozukları tekrar dene\"ye basın; başka bir sürücü de deneyebilirsiniz.",
                           "Rescue finished. {0} sectors could not be read ({1}). Clean the disc and press \"Retry bad sectors\"; another drive may also help."),
        ["Stopped"] = ("Durduruldu. Kaldığınız yer kaydedildi.", "Stopped. Progress is saved."),
        ["ChooseFolderFirst"] = ("Lütfen önce kayıt yerini seçin.", "Please choose where to save first."),

        ["Source"] = ("Kaynak", "Source"),
        ["SourceLive"] = ("Diskten canlı okuma ({0}:)", "Live from disc ({0}:)"),
        ["SourceImage"] = ("Kalıp: {0}", "Image: {0}"),
        ["NoContent"] = ("Gösterilecek içerik yok. Bir disk takın veya kalıp açın.", "Nothing to show. Insert a disc or open an image."),
        ["OpenImage"] = ("Kalıp aç...", "Open image..."),
        ["Extract"] = ("Dosyaları çıkar...", "Extract files..."),
        ["SaveIso"] = ("ISO kalıbı kaydet...", "Save ISO image..."),
        ["Mount"] = ("Sanal CD olarak aç", "Open as virtual CD"),
        ["Unmount"] = ("Sanal CD'yi çıkar", "Eject virtual CD"),
        ["Files"] = ("{0} dosya, {1} klasör, {2}", "{0} files, {1} folders, {2}"),
        ["DamagedFiles"] = ("  •  {0} hasarlı dosya", "  •  {0} damaged files"),
        ["RescueFirst"] = ("Bu işlem kurtarılmış kalıptan yapılır. Önce \"Kurtarma\" sekmesinden diski kurtarın.", "This works on the rescued image. Rescue the disc first on the \"Rescue\" tab."),
        ["Extracted"] = ("{0} dosya çıkarıldı. Hasarlı: {1}.", "{0} files extracted. Damaged: {1}."),
        ["IsoExact"] = ("ISO kaydedildi (sektörü sektörüne birebir kopya).", "ISO saved (exact sector copy)."),
        ["IsoRebuilt"] = ("ISO kaydedildi. Bu bir Video CD olduğu için tüm dosyalar tam olarak yeni bir ISO'ya yerleştirildi (videolar .mpg). Ham tam kopya .bin/.cue olarak da duruyor.",
                          "ISO saved. This is a Video CD, so every file was placed in full into a new ISO (videos as .mpg). The complete raw copy is also kept as .bin/.cue."),
        ["IsoCopy"] = ("ISO kaydedildi.", "ISO saved."),
        ["Mounted"] = ("Sanal CD bağlandı: {0}:", "Virtual CD mounted: {0}:"),
        ["NoFs"] = ("Dosya sistemi okunamadı: {0}", "File system could not be read: {0}"),
        ["Working"] = ("Çalışıyor...", "Working..."),
        ["ColName"] = ("Ad", "Name"),
        ["ColSize"] = ("Boyut", "Size"),
        ["ColState"] = ("Durum", "State"),
        ["StateOk"] = ("Sağlam", "Intact"),
        ["StateBad"] = ("{0} bozuk sektör", "{0} bad sectors"),
        ["StateUnknown"] = ("—", "—"),

        ["Videos"] = ("Videolar", "Videos"),
        ["AddVideos"] = ("Video ekle...", "Add videos..."),
        ["FromDisc"] = ("Diskteki videoları ekle", "Add videos from disc"),
        ["Quality"] = ("Kalite", "Quality"),
        ["QOriginal"] = ("Orijinal çözünürlük (temizlenmiş)", "Original resolution (cleaned)"),
        ["Q720"] = ("HD 720p — büyütülmüş + keskin", "HD 720p — upscaled + sharpened"),
        ["Q1080"] = ("Full HD 1080p — en iyi (yavaş)", "Full HD 1080p — best (slow)"),
        ["Codec"] = ("Sıkıştırma", "Codec"),
        ["CodecH264"] = ("H.264 (her yerde oynar)", "H.264 (plays everywhere)"),
        ["CodecH265"] = ("H.265 (yarı boyut)", "H.265 (half the size)"),
        ["OutputFolder"] = ("MP4 kayıt yeri", "MP4 output folder"),
        ["Convert"] = ("MP4'e çevir", "Convert to MP4"),
        ["FfmpegFound"] = ("FFmpeg hazır", "FFmpeg ready"),
        ["FfmpegMissing"] = ("Video çevirmek için FFmpeg gerekli (ücretsiz, ~100 MB).", "FFmpeg is needed to convert videos (free, ~100 MB)."),
        ["FfmpegDownload"] = ("FFmpeg'i indir", "Download FFmpeg"),
        ["Downloading"] = ("İndiriliyor... %{0:N0}", "Downloading... {0:N0}%"),
        ["Converting"] = ("Çevriliyor: {0}", "Converting: {0}"),
        ["Preparing"] = ("Hazırlanıyor: {0}", "Preparing: {0}"),
        ["Converted"] = ("Bitti: {0}", "Done: {0}"),
        ["VideoWaiting"] = ("Bekliyor", "Waiting"),
        ["VideoFailed"] = ("Hata", "Failed"),
        ["VideoHint"] = ("Eski görüntü taramalı satırlardan arındırılır, parazit azaltılır, keskinleştirilir ve yüksek çözünürlüğe büyütülür. Bozuk sektörlü videolar da hata vermeden işlenir.",
                         "Old footage is deinterlaced, denoised, sharpened and upscaled. Videos with bad sectors are processed without failing."),
        ["Remove"] = ("Kaldır", "Remove"),

        ["HelpTitle"] = ("Çizik bir diski kurtarmak için", "To rescue a scratched disc"),
        ["Help1"] = ("1. Diski yumuşak, tüy bırakmayan bir bezle ORTADAN DIŞA doğru düz çizgilerle silin (asla daire çizerek değil).", "1. Wipe the disc with a soft lint-free cloth in straight lines FROM THE CENTRE OUTWARDS (never in circles)."),
        ["Help2"] = ("2. Kirliyse ılık su ve bir damla bulaşık deterjanıyla yıkayıp kurulayın.", "2. If dirty, wash with lukewarm water and a drop of dish soap, then dry."),
        ["Help3"] = ("3. Kurtarmayı başlatın. Program önce kolay okunan her yeri alır, bozuk bölgeleri sona bırakır.", "3. Start the rescue. The program first takes everything that reads easily and leaves damaged areas for last."),
        ["Help4"] = ("4. Bozuk sektör kalırsa okuma hızını 4x veya 1x yapıp \"Bozukları tekrar dene\"ye basın. Başka bir CD/DVD sürücüsü bazen okuyamadığını okur — aynı kayıt yerini seçerseniz sadece eksikler okunur.", "4. If bad sectors remain, set the speed to 4x or 1x and press \"Retry bad sectors\". Another drive can sometimes read what this one cannot — choose the same save place and only the missing parts are read."),
        ["Help5"] = ("5. Derin çiziklerde son çare: diş macunu veya plastik parlatıcıyla ortadan dışa doğru hafif cilalayın.", "5. Last resort for deep scratches: polish gently with toothpaste or plastic polish, centre outwards."),
        ["HelpSectors"] = ("Sektör sektör kurtarma nedir? Disk 2 KB'lık on binlerce küçük parçadan (sektör) oluşur. Windows bir dosyada tek bir parçayı okuyamazsa bütün dosyayı kaybedersiniz. DVD 360 her parçayı ayrı okur, okunamayanı işaretleyip geçer ve sonra tekrar tekrar dener — böylece dosyanın geri kalanı kurtulur.",
                                "What is sector-by-sector rescue? A disc is made of tens of thousands of small 2 KB pieces (sectors). If Windows cannot read one piece of a file, you lose the whole file. DVD 360 reads every piece on its own, marks the unreadable ones, moves on and comes back to retry — so the rest of the file is saved."),
        ["About"] = ("Hakkında", "About"),
        ["AboutText"] = ("DVD 360 — ücretsiz ve açık kaynak (MIT lisansı).\nGeliştirici: Ömer Çarnaçar", "DVD 360 — free and open source (MIT license).\nDeveloper: Ömer Çarnaçar"),
        ["Error"] = ("Hata", "Error"),
        ["RescueSelected"] = ("Seçilenleri kurtar...", "Rescue selected..."),
        ["SelectAll"] = ("Tümünü seç", "Select all"),
        ["SelectNone"] = ("Seçimi temizle", "Clear selection"),
        ["PickFiles"] = ("Kurtarmak istediğiniz dosyaları listeden işaretleyin.", "Tick the files you want to rescue in the list."),
        ["SelectedStart"] = ("Seçilen {0} dosya ({1}) sektör sektör okunuyor...", "Reading the {0} selected files ({1}) sector by sector..."),
        ["SelectedOnly"] = ("yalnızca seçili dosyalar", "selected files only"),
        ["SelectedRead"] = ("Seçili dosyaların sektörleri okundu, dosyalar çıkarılıyor.", "The selected files' sectors were read; extracting the files."),
        ["SelectedDamaged"] = ("Bu dosyalarda okunamayan sektör kaldı (videolarda kısa bir takılma olarak görünür). Diski temizleyip \"Bozukları tekrar dene\" ile tekrar deneyebilirsiniz:\n\n{0}",
                               "These files still have unreadable sectors (in videos this shows as a short glitch). Clean the disc and use \"Retry bad sectors\":\n\n{0}"),
        ["AutoIso"] = ("Bitince ISO olarak da kaydet", "Also save as ISO when finished"),
        ["ReadFirst"] = ("Önce diski okuyun: \"Kurtarmayı başlat\"a basın. ISO, okunan kopyadan oluşturulur.", "Read the disc first: press \"Start rescue\". The ISO is made from the rescued copy."),
        ["StopFirst"] = ("Okuma sürüyor. ISO kaydetmek için önce \"Durdur\"a basın veya bitmesini bekleyin.", "Reading is in progress. Press \"Stop\" or wait for it to finish before saving the ISO."),
        ["IsoPartial"] = ("Disk henüz %{0:0.#} okundu. Okunamayan yerler ISO'da boş kalır. Yine de kaydedilsin mi?", "Only {0:0.#}% of the disc has been read. Unread parts will be empty in the ISO. Save anyway?"),
        ["IsoSavedTo"] = ("ISO kalıbı kaydedildi: {0}", "ISO image saved: {0}"),
        ["FoundEarlier"] = ("Bu diskin önceki kurtarması bulundu: {0} — \"Devam et\"e basınca kaldığı yerden sürer.", "Found an earlier rescue of this disc: {0} — press \"Continue\" to pick up where it stopped."),
        ["NoFileHere"] = ("Dosya yok (dosya sistemi / boş alan)", "No file here (file system / free space)"),
        ["SectorState_Untried"] = ("Henüz okunmadı", "Not read yet"),
        ["SectorState_Ok"] = ("Okundu", "Read"),
        ["SectorState_NeedsRetry"] = ("Tekrar denenecek", "To retry"),
        ["SectorState_Bad"] = ("Bozuk", "Bad"),
        ["SectorState_Skipped"] = ("Diskte yok", "Not on disc"),
    };
}
