using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using UnityEngine;

namespace GorillaFlag
{
    [BepInPlugin("gorillaflag", "GorillaFlag", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private const string HedefAdi = "FlagPrideStandard";
        private static readonly string[] YontemAdlari = { "Array", "Keyword", "Shader" };

        // Atlas (256x256) icindeki bayrak bolgeleri. PNG koordinatlari (sol ust orijin).
        private struct Bolge
        {
            public float x0, x1, y0, y1;       // bayragin asil dikdortgeni
            public int px0, px1, py0, py1;   // tasma (dolgu) dahil boyanacak alan
            public bool ust;
        }

        private static readonly Bolge Ust = new Bolge { x0 = 103.95f, x1 = 237.76f, y0 = 17.39f, y1 = 118.5f, px0 = 70, px1 = 248, py0 = 0, py1 = 128, ust = true };
        private static readonly Bolge Alt = new Bolge { x0 = 103.91f, x1 = 237.72f, y0 = 136.99f, y1 = 238.12f, px0 = 96, px1 = 256, py0 = 128, py1 = 256, ust = false };

        // Cevirme ayarlari (menuden degistirilir)
        private bool ustX = true, ustY = false, altX = false, altY = false;

        private bool resimEkle;
        // Dosya penceresi: arka planda hazir bekleyen bir PowerShell sureci acar (ilk acilis beklemesi olmaz).
        // Pencere ekranin ortasinda, her seyin ustunde acilir. Oyunu dondurmaz.
        private System.Diagnostics.Process dlgSurec;
        private readonly Queue<string> dlgCikti = new Queue<string>();
        private bool dlgBekliyor;
        private float dlgZaman;

        private void DosyaSureciHazirla()
        {
            if (dlgSurec != null && !dlgSurec.HasExited) return;
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -NoLogo -STA -ExecutionPolicy Bypass -WindowStyle Hidden -Command -");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                dlgSurec = new System.Diagnostics.Process();
                dlgSurec.StartInfo = psi;
                dlgSurec.OutputDataReceived += (o, ev) => { if (ev.Data != null) lock (dlgCikti) dlgCikti.Enqueue(ev.Data); };
                dlgSurec.Start();
                dlgSurec.BeginOutputReadLine();
                // pencere kutuphanesini simdiden yukle: sonra aninda acilsin
                dlgSurec.StandardInput.WriteLine("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; [System.Windows.Forms.Application]::EnableVisualStyles()");
                dlgSurec.StandardInput.Flush();
            }
            catch (Exception e)
            {
                dlgSurec = null;
                Logger.LogWarning("File window helper could not start: " + e.Message);
            }
        }

        private void DosyaPenceresiAc()
        {
            if (dlgBekliyor) return;
            DosyaSureciHazirla();
            if (dlgSurec == null) { durum = Dil("Could not open file window: ") + "powershell"; return; }
            try
            {
                string kod =
                    "$o=New-Object System.Windows.Forms.Form;$o.TopMost=$true;$o.ShowInTaskbar=$false;$o.FormBorderStyle='None';" +
                    "$o.Opacity=0;$o.StartPosition='CenterScreen';$o.Size=New-Object System.Drawing.Size(1,1);$o.Show();$o.Activate();" +
                    "$f=New-Object System.Windows.Forms.OpenFileDialog;" +
                    "$f.Filter='Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*';$f.Title='Select a flag image';" +
                    "$r='';if($f.ShowDialog($o) -eq 'OK'){$r=$f.FileName};$o.Close();" +
                    "[Console]::Out.WriteLine('@@GF@@'+$r);[Console]::Out.Flush()";
                lock (dlgCikti) dlgCikti.Clear();
                dlgSurec.StandardInput.WriteLine(kod);
                dlgSurec.StandardInput.Flush();
                dlgBekliyor = true;
                dlgZaman = Time.time;
                durum = Dil("Choose an image in the window...");
            }
            catch (Exception e)
            {
                try { dlgSurec.Kill(); } catch (Exception) { }
                dlgSurec = null;
                dlgBekliyor = false;
                durum = Dil("Could not open file window: ") + e.Message;
                Logger.LogError("File dialog error: " + e);
            }
        }

        private void DosyaPenceresiKontrol()
        {
            if (!dlgBekliyor) return;
            if (dlgSurec == null || dlgSurec.HasExited)
            {
                dlgBekliyor = false; dlgSurec = null; sapDosyaModu = false;
                durum = Dil("Could not open file window: ") + "powershell";
                return;
            }
            string yol = null;
            lock (dlgCikti)
            {
                while (dlgCikti.Count > 0)
                {
                    string satir = dlgCikti.Dequeue();
                    int i = satir.IndexOf("@@GF@@", StringComparison.Ordinal);
                    if (i >= 0) { yol = satir.Substring(i + 6).Trim(); break; }
                }
            }
            if (yol == null) return;
            dlgBekliyor = false;

            if (string.IsNullOrEmpty(yol)) { durum = ""; sapDosyaModu = false; return; }
            if (sapDosyaModu) { sapDosyaModu = false; SapResimYukle(yol, true); return; }
            ResimEkle(yol);
        }

        void OnDestroy()
        {
            try { if (dlgSurec != null && !dlgSurec.HasExited) dlgSurec.Kill(); } catch (Exception) { }
        }

        private void ResimEkle(string yol)
        {
            try
            {
                Type tip = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                var yukle = tip.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]) });
                var enc = tip.GetMethod("EncodeToPNG", new Type[] { typeof(Texture2D) });

                Texture2D src = new Texture2D(2, 2);
                yukle.Invoke(null, new object[] { src, File.ReadAllBytes(yol) });

                // ortadan 3:2 kirp, 600x400 yap
                float hedef = 4f / 3f;
                float oran = (float)src.width / src.height;
                Vector2 scale = new Vector2(1f, 1f);
                if (oran > hedef) scale.x = hedef / oran; else scale.y = oran / hedef;
                Vector2 ofs = new Vector2((1f - scale.x) * 0.5f, (1f - scale.y) * 0.5f);

                RenderTexture rt = RenderTexture.GetTemporary(600, 450, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt, scale, ofs);
                RenderTexture eski = RenderTexture.active;
                RenderTexture.active = rt;
                Texture2D o = new Texture2D(600, 450, TextureFormat.RGBA32, false);
                o.ReadPixels(new Rect(0, 0, 600, 450), 0, 0);
                o.Apply();
                RenderTexture.active = eski;
                RenderTexture.ReleaseTemporary(rt);

                byte[] png = (byte[])enc.Invoke(null, new object[] { o });
                Destroy(o);
                Destroy(src);

                string ad = Path.GetFileNameWithoutExtension(yol).ToLower().Replace(" ", "_");
                foreach (char c in Path.GetInvalidFileNameChars()) ad = ad.Replace(c.ToString(), "");
                if (ad == "") ad = "flag";

                if (HazirKat(aktifKat)) aktifKat = KatOzel;   // hazir kategorilerdeyken eklenen resim Custom'a gider
                string katAdi = katListesi[aktifKat];
                string klasor = KatKlasor(aktifKat);
                Directory.CreateDirectory(klasor);
                File.WriteAllBytes(Path.Combine(klasor, ad + ".png"), png);

                PngleriYukle();
                for (int k = 0; k < katListesi.Count; k++) if (katListesi[k] == katAdi) aktifKat = k;
                arama = "";
                kaydirma = Vector2.zero;
                for (int i = 0; i < isimler.Count; i++)
                    if (kategoriler[i] == aktifKat && isimler[i] == ad && refler[i] == null) { Sec(i); break; }
                durum = Dil("Added: ") + AdTam(ad);
            }
            catch (Exception e)
            {
                durum = Dil("Could not add image: ") + e.Message;
                Logger.LogError("Add image error: " + e);
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private readonly bool[] onceki = new bool[4];

        // ---- Tus atamalari (Settings > KEYBINDS) ----
        private int menuTus = 0x46;   // F
        private BepInEx.Configuration.ConfigEntry<int> menuTusCfg;
        // saplama tusu secenekleri (VR kumanda)
        private static readonly string[] SaplaTusAdlari = { "Y / B", "X / A", "Y", "B", "X", "A" };
        private int saplaTus = 0;
        private BepInEx.Configuration.ConfigEntry<int> saplaTusCfg;
        // saplama: 2 ayri tus yuvasi (0 yok, 1 Y, 2 X, 3 B, 4 A)
        private static readonly string[] KolTusAdlari = { "-", "Y", "X", "B", "A" };
        private static readonly string[] KolTusOzellik = { null, "leftControllerSecondaryButton", "leftControllerPrimaryButton", "rightControllerSecondaryButton", "rightControllerPrimaryButton" };
        private readonly int[] saplaYuva = { 1, 3 };
        private readonly BepInEx.Configuration.ConfigEntry<int>[] saplaYuvaCfg = new BepInEx.Configuration.ConfigEntry<int>[2];
        private int yuvaDinle = -1;            // tus bekleyen yuva (-1 yok)
        private float yuvaDinleZaman;
        private readonly bool[] yuvaOnceki = new bool[5];

        private bool KolTus(int id)
        {
            if (id <= 0 || id >= KolTusOzellik.Length || tPoller == null) return false;
            object inst = UyeAl(null, tPoller, "instance");
            if (inst == null) return false;
            object o = UyeAl(inst, tPoller, KolTusOzellik[id]);
            return o is bool && (bool)o;
        }

        private void YuvaDinleBasla(int y)
        {
            if (yuvaDinle == y) { saplaYuva[y] = 0; if (saplaYuvaCfg[y] != null) saplaYuvaCfg[y].Value = 0; yuvaDinle = -1; return; }   // tekrar bas: bosalt
            yuvaDinle = y;
            yuvaDinleZaman = Time.time;
            for (int i = 1; i < 5; i++) { try { yuvaOnceki[i] = KolTus(i); } catch (Exception) { yuvaOnceki[i] = false; } }
        }

        private void YuvaDinleKare()
        {
            if (yuvaDinle < 0) return;
            if (Time.time - yuvaDinleZaman > 8f) { yuvaDinle = -1; return; }
            for (int i = 1; i < 5; i++)
            {
                bool b = false;
                try { b = KolTus(i); } catch (Exception) { }
                if (b && !yuvaOnceki[i])
                {
                    saplaYuva[yuvaDinle] = i;
                    if (saplaYuvaCfg[yuvaDinle] != null) saplaYuvaCfg[yuvaDinle].Value = i;
                    yuvaDinle = -1;
                    if (!saplamaAcik) SaplamaSec(true);   // tus atadiysan saplamak istiyorsun: kapaliysa ac
                    return;
                }
                yuvaOnceki[i] = b;
            }
        }
        private bool tusDinle;
        private readonly bool[] dinleOnceki = new bool[256];

        private static string TusAdi(int vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            switch (vk)
            {
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x10: case 0xA0: case 0xA1: return "Shift";
                case 0x11: case 0xA2: case 0xA3: return "Ctrl";
                case 0x12: case 0xA4: case 0xA5: return "Alt";
                case 0x14: return "Caps";
                case 0x20: return "Space";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "Left";
                case 0x26: return "Up";
                case 0x27: return "Right";
                case 0x28: return "Down";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x6A: return "Num*";
                case 0x6B: return "Num+";
                case 0x6D: return "Num-";
                case 0x6E: return "Num.";
                case 0x6F: return "Num/";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
            }
            return "0x" + vk.ToString("X2");
        }

        private void TusDinleBasla()
        {
            tusDinle = true;
            for (int vk = 0; vk < 256; vk++) dinleOnceki[vk] = (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        // dinleme modunda: ilk basilan yeni tusu menu tusu yap (Esc iptal)
        private void TusDinleKare()
        {
            for (int vk = 0x08; vk < 0xFF; vk++)
            {
                if (vk == 0x10 || vk == 0x11 || vk == 0x12) continue;   // genel Shift/Ctrl/Alt yerine sag/sol olanlar
                bool simdi = (GetAsyncKeyState(vk) & 0x8000) != 0;
                bool yeni = simdi && !dinleOnceki[vk];
                dinleOnceki[vk] = simdi;
                if (!yeni) continue;
                tusDinle = false;
                if (vk != 0x1B)
                {
                    menuTus = vk;
                    if (menuTusCfg != null) menuTusCfg.Value = vk;
                }
                onceki[0] = true;   // ayni basis menuyu kapatmasin
                return;
            }
        }

        private bool Basildi(int i, int vk)
        {
            bool simdi = Application.isFocused && (GetAsyncKeyState(vk) & 0x8000) != 0;   // baska pencerede yazarken tetiklenmesin
            bool sonuc = simdi && !onceki[i];
            onceki[i] = simdi;
            return sonuc;
        }

        private class Kayit
        {
            public Material mat;
            public Shader shader;
            public string[] keywords;
            public float dilim;
            public Dictionary<string, Texture> tex = new Dictionary<string, Texture>();
            public Dictionary<string, float> floatlar = new Dictionary<string, float>();
            public string uygulanan = "";
        }

        private readonly Dictionary<Renderer, Kayit> kayitlar = new Dictionary<Renderer, Kayit>();
        private readonly Dictionary<string, Texture2D> atlaslar = new Dictionary<string, Texture2D>();
        private readonly Dictionary<string, Texture2DArray> diziler = new Dictionary<string, Texture2DArray>();

        private readonly List<Texture2D> bayraklar = new List<Texture2D>();
        private readonly List<string> isimler = new List<string>();
        private readonly List<int> kategoriler = new List<int>();   // 0 countries, 1 pride, 2+ kullanicinin kategorileri
        private readonly List<string> refler = new List<string>();     // kullanici kategorisindeki hazir bayrak referansi ("countries/turkey"), resimse null
        private bool secimModu;            // kategoriye hazir bayrak secme ekrani
        private int secimKat = -1;         // -1: yeni kategori, >=2: duzenlenen kategori
        private readonly HashSet<string> secilenler = new HashSet<string>();

        // Hangi kozmetik degissin: 0 Pride Flag, 1 Trans Pride Flag, 2 Private (sadece bende, yerel kopya), 3 None (kapali)
        private static readonly string[] KozAdlari = { "Pride Flag", "Trans Pride Flag", "Private", "None" };
        // Variant butonunda tiklama sirasi: None -> Pride -> Trans (Private artik ayri bir ac/kapa)
        private static readonly int[] KozSira = { 3, 0, 1 };
        private int kozmetik = 3;
        private BepInEx.Configuration.ConfigEntry<int> kozCfg;
        // Private: sadece bende gorunen yerel kopya. Variant'tan bagimsiz (Variant None olsa da kapanmaz)
        private bool gizli = false;
        private BepInEx.Configuration.ConfigEntry<bool> gizliCfg;

        private void GizliSec(bool ac)
        {
            if (ac == gizli) return;
            gizli = ac;
            if (gizliCfg != null) gizliCfg.Value = ac;
            YerelKaldir();
            durum = "";
            yerelZaman = 0f;
            foreach (var kv in kayitlar) kv.Value.uygulanan = "";
            YerelGuncelle();
            if (Etkin) Sec(secili);
        }

        private bool YerelMi(Renderer r)
        {
            return gizli && yerelKopya != null && r != null && r.transform.IsChildOf(yerelKopya.transform);
        }
        private GameObject yerelKopya;
        private Mesh yerelMesh;
        private Vector3[] yerelTaban, yerelCalisma;
        private float[] yerelMesafe;
        private float yerelMaks, yerelSapX, yerelSapZ;
        private float yerelZaman;

        private void KozmetikSec(int i)
        {
            if (i == kozmetik) return;
            foreach (var kv in kayitlar) Geri(kv.Value);
            kayitlar.Clear();
            kozmetik = i;
            if (kozCfg != null) kozCfg.Value = i;
            YerelKaldir();
            durum = "";
            yerelZaman = 0f;
            YerelGuncelle();
            if (Etkin) Sec(secili);
        }

        private void YerelKaldir()
        {
            AyakSil(ref yerelAyak); yerelAyakBoy = -1f;
            if (yerelKopya != null) Destroy(yerelKopya);
            yerelKopya = null;
            if (yerelMesh != null) Destroy(yerelMesh);
            yerelMesh = null;
            yerelRgo = null;
            tutuluyor = false;
            tutanEl = null;
            sapli = false;
            ucuyor = false;
            oncekiUcVar = false;
            yerelBoingComp = null;
            yerelKemikA = yerelKemikF = null;
            agirMod = false;
            dokunmaKuvvet = Vector3.zero;
        }

        // "Sadece bende": oyundaki bayrak kozmetigi takili degilken, yerel oyuncunun sirtina
        // sadece bu bilgisayarda gorunen bir kopya koyar. Sunucuya hicbir sey gitmez.
        private SkinnedMeshRenderer yerelKaynak;
        private Transform yerelSlot, yerelRgo, yerelAnchor;
        private Vector3 yerelLokPos;
        private Quaternion yerelLokRot = Quaternion.identity;
        private float yerelSapAltY, yerelSapUstY;
        private BepInEx.Configuration.ConfigEntry<string> pozCfg;
        private bool pozVar;
        private Vector3 kayitPos;
        private Quaternion kayitRot = Quaternion.identity;
        private bool tutuluyor;
        // firlatma: birakirken el hizliysa bayrak mizrak gibi ucar; ucu onde ve sert gelirse saplanir, yoksa duser
        private const float FirlatEsik = 2.5f, FirlatCarpan = 1.25f;
        private bool ucuyor;
        private Vector3 ucusHiz, tutHiz, tutOnceki;
        private bool tutHizVar;
        private float ucusZaman;
        private Transform tutanEl;
        private int tutanH = -1;
        private bool donusSuruyor;
        private Vector3 donusBasPos;
        private Quaternion donusBasRot;
        private float donusBasZaman;
        private Vector3 tutOfsPos;
        private Quaternion tutOfsRot;
        private readonly bool[] oncekiGrab = new bool[2];

        // ---- Bayragi yere saplama (sadece Private kopya, sadece bende) ----
        // Y veya B basili tutulurken sapin ucu yere yeterince sert vurulursa bayrak oraya saplanir.
        private float SaplamaHizi = 1.5f;            // m/s: bundan yavas vurus saplamaz (Settings > PLANT STRENGTH)
        private BepInEx.Configuration.ConfigEntry<float> saplamaHiziCfg;
        private static readonly float[] SaplamaHizlari = { 0.8f, 1.1f, 1.5f, 2.2f, 3.0f };
        private static readonly string[] SaplamaHizAd = { "Softest", "Soft", "Normal", "Hard", "Hardest" };
        private void SaplamaHiziSec(float v) { SaplamaHizi = v; if (saplamaHiziCfg != null) saplamaHiziCfg.Value = v; }
        // firlatilan bayrak sadece ucuyla sert gelirse saplanir; yavas gelirse devrilip yatar
        private float FirlatSaplamaHizi { get { return SaplamaHizi + 3f; } }
        private bool saplamaAcik = true;
        private BepInEx.Configuration.ConfigEntry<bool> saplamaCfg;

        private void SaplamaSec(bool ac)
        {
            saplamaAcik = ac;
            if (saplamaCfg != null) saplamaCfg.Value = ac;
            if (!ac && sapli) { SapliCoz(); SirtaKoy(); }   // kapatinca saplanmis bayrak sirta doner
        }
        private bool sapli;
        private string sapliOda = "";
        private Vector3 oncekiUc;
        private bool oncekiUcVar;
        private Type tFoton;

        private bool XBasili()
        {
            try
            {
                if (tPoller == null) return false;
                object inst = UyeAl(null, tPoller, "instance");
                if (inst == null) return false;
                // Settings > KEYBINDS'te secilen tus (varsayilan Y / B)
                System.Func<string, bool> bas = ad => { object o = UyeAl(inst, tPoller, ad); return o is bool && (bool)o; };
                if (yuvaDinle >= 0) return false;      // tus atarken saplama yok
                bool sonuc = false;
                for (int y = 0; y < 2; y++)
                {
                    int id = saplaYuva[y];
                    if (id > 0 && id < KolTusOzellik.Length && bas(KolTusOzellik[id])) sonuc = true;
                }
                return sonuc;
            }
            catch (Exception) { }
            return false;
        }

        // ---------- GECICI: ekran yeri olcumu ----------
        private float olcumZaman = 0f;
        private void EkranYeriOlc()
        {
            var ic = CultureInfo.InvariantCulture;
            Func<Vector3, string> V = v => v.x.ToString("0.000", ic) + ", " + v.y.ToString("0.000", ic) + ", " + v.z.ToString("0.000", ic);
            var sb = new StringBuilder();
            sb.Append("[SCREEN SPOT] scenes:");
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (sc.isLoaded) sb.Append(" " + sc.name);
            }
            Logger.LogInfo(sb.ToString());
            Transform kafa = Camera.main != null ? Camera.main.transform : null;
            if (kafa != null) Logger.LogInfo("[SCREEN SPOT] player head pos=(" + V(kafa.position) + ") fwd=(" + V(kafa.forward) + ")");
            for (int h = 1; h >= 0; h--)
            {
                Transform el = El(h);
                if (el == null) continue;
                string ad = h == 1 ? "right hand" : "left hand";
                Logger.LogInfo("[SCREEN SPOT] " + ad + " pos=(" + V(el.position) + ") fwd=(" + V(el.forward) + ") up=(" + V(el.up) + ")");
                RaycastHit hit;
                if (ZeminBul(el.position, el.forward, 15f, out hit))
                    Logger.LogInfo("[SCREEN SPOT] " + ad + " HIT point=(" + V(hit.point) + ") normal=(" + V(hit.normal) + ") dist=" + hit.distance.ToString("0.00", ic) + " obj=" + Yol(hit.collider.transform));
                else Logger.LogInfo("[SCREEN SPOT] " + ad + " hit nothing within 15 m");
            }
            if (kafa != null)
            {
                RaycastHit hk;
                if (ZeminBul(kafa.position, kafa.forward, 15f, out hk))
                    Logger.LogInfo("[SCREEN SPOT] head HIT point=(" + V(hk.point) + ") normal=(" + V(hk.normal) + ") dist=" + hk.distance.ToString("0.00", ic) + " obj=" + Yol(hk.collider.transform));
            }
        }

        // ================= DUVAR EKRANI (oyun ici menu, Forest - City tuneli) =================
        // olculen yer: kafanin baktigi nokta (WalkSim), tunel duvari
        // 0: Forest - City tuneli, 1: City - Wardrobe Hut duvari
        private static readonly Vector3[] EkranNoktalar = { new Vector3(-64.063f, 12.710f, -84.747f), new Vector3(-45.840f, 16.847f, -100.735f) };
        private static readonly Vector3[] EkranNormaller = { new Vector3(0.979f, -0.179f, -0.099f), new Vector3(-0.017f, -0.004f, -1.000f) };
        private static readonly float[] EkranSagaKaydir = { 0.10f, 0.15f };
        private static readonly float[] EkranOneAl = { 0f, 0.035f };       // duvardan ekstra one alma (metre)
        private readonly GameObject[] ekranlar = new GameObject[2];
        private readonly RectTransform[] ekIcerikler = new RectTransform[2];
        private int ekranNo = 0;
        private const float EkW = 476f, EkH = 660f;
        private const float EkMetre = 0.5f;            // ekran genisligi (metre)
        private GameObject ekranGo;
        private RectTransform ekranIcerik;
        private Font ekranFont;
        private Sprite ekranYuvarlak;
        private float ekranDeneme;
        private string ekranImza = "";
        private float ekranImzaZaman;
        private int eSekme = 0, eBayrakSayfa = 0, eSapSayfa = 0, eAyarSayfa = 0;
        private int eSilBekle = -1; private float eSilZaman;
        private float eSifirZaman = -10f;
        private readonly List<KeyValuePair<Rect, Action>> eTiklar = new List<KeyValuePair<Rect, Action>>();
        private readonly bool[] eBasili = new bool[8];
        private readonly bool[] eHazir = new bool[8];

        // basan parmaklar: 0/1 sol/sag isaret parmagi (oyunun dugme collider'i), 2/3 sol/sag orta parmak (gorillanin parmak kemigi)
        private readonly Transform[] ortaUc = new Transform[2];
        private readonly float[] ortaArama = new float[2];
        private bool BasanNokta(int k, out Vector3 p)
        {
            p = Vector3.zero;
            if (k < 2)
            {
                Transform t = ParmakUcu(k);
                if (t == null) return false;
                p = t.position;
                return true;
            }
            Transform o = OrtaParmak(k - 2);
            if (o == null) return false;
            p = o.parent != null ? o.position + (o.position - o.parent.position) * 0.5f : o.position;   // eklemin biraz ilerisi = parmak ucu
            return true;
        }

        private Transform OrtaParmak(int h)
        {
            if (ortaUc[h] != null) return ortaUc[h];
            if (Time.time < ortaArama[h]) return null;
            ortaArama[h] = Time.time + 5f;
            Transform kok = YerelRigKok();
            if (kok == null) return null;
            Transform isaret = ParmakUcu(h);
            // gorillanin iskeletindeki el kemigi (VRRig'in IK hedefi degil): adinda "hand" ve sol/sag isareti olan, cocugu olan kemik
            Transform el = null; int elPuan = -1;
            foreach (Transform t in kok.GetComponentsInChildren<Transform>(true))
            {
                if (t.childCount == 0) continue;
                string n = t.name.ToLowerInvariant();
                if (!n.Contains("hand") || n.Contains("ik") || n.Contains("constraint") || n.Contains("anchor") || n.StartsWith("gorillaflag")) continue;
                if (!YanUyar(n, h)) continue;
                string yol = Yol(t);
                if (yol.Contains("VR Constraints")) continue;
                int puan = (yol.Contains("/rig/") ? 2 : 0) + (n == (h == 0 ? "hand.l" : "hand.r") || n == (h == 0 ? "hand_l" : "hand_r") ? 3 : 0);
                if (puan > elPuan) { el = t; elPuan = puan; }
            }
            if (el == null) { Logger.LogInfo("Middle finger " + (h == 0 ? "L" : "R") + ": hand bone not found"); return null; }
            Transform en = null; int enPuan = -1; float enUzak = 0f;
            List<string> adlar = new List<string>();
            foreach (Transform t in el.GetComponentsInChildren<Transform>(true))
            {
                if (t == el) continue;
                string n = t.name.ToLowerInvariant();
                if (n.StartsWith("gorillaflag") || t.GetComponent<Renderer>() != null) continue;
                if (adlar.Count < 40) adlar.Add(t.name);
                if (n.Contains("thumb") || n.Contains("index") || n.Contains("anchor") || n.Contains("audio")) continue;
                float uzak = Vector3.Distance(t.position, el.position);
                if (uzak > 0.3f) continue;
                if (isaret != null && Vector3.Distance(t.position, isaret.position) < 0.015f) continue;
                int puan = n.Contains("middle") ? 4 : (n.Contains("finger") || n.Contains("f_") || n.Contains("ring") || n.Contains("pinky")) ? 2 : 0;
                if (t.childCount == 0) puan += 1;    // parmagin en uc kemigi
                if (puan > enPuan || (puan == enPuan && uzak > enUzak)) { en = t; enPuan = puan; enUzak = uzak; }
            }
            ortaUc[h] = en;
            Logger.LogInfo("Middle finger " + (h == 0 ? "L" : "R") + ": hand=" + Yol(el) + " -> " + (en != null ? en.name : "not found") + "  (bones: " + string.Join(", ", adlar.ToArray()) + ")");
            return en;
        }

        private static bool YanUyar(string n, int h)
        {
            if (h == 0) return n.EndsWith(".l") || n.EndsWith("_l") || n.EndsWith(" l") || n.Contains("left") || n.EndsWith(".l.001");
            return n.EndsWith(".r") || n.EndsWith("_r") || n.EndsWith(" r") || n.Contains("right") || n.EndsWith(".r.001");
        }

        // oyunun kendi dugmelerine basan parmak ucu (GorillaTagger.left/rightHandTriggerCollider), yoksa el
        private readonly Transform[] parmakOnbellek = new Transform[2];
        private Transform ParmakUcu(int h)
        {
            if (parmakOnbellek[h] != null) return parmakOnbellek[h];   // her karede yansima yapma
            Transform t = ParmakUcuBul(h);
            if (t != null && t != El(h)) parmakOnbellek[h] = t;
            return t;
        }

        private Transform ParmakUcuBul(int h)
        {
            try
            {
                if (tTagger != null)
                {
                    object inst = UyeAl(null, tTagger, "Instance");
                    if (inst != null)
                    {
                        object o = UyeAl(inst, tTagger, h == 0 ? "leftHandTriggerCollider" : "rightHandTriggerCollider");
                        GameObject g = o as GameObject;
                        if (g != null) return g.transform;
                        Component c = o as Component;
                        if (c != null) return c.transform;
                    }
                }
            }
            catch (Exception) { }
            return El(h);
        }

        // oyundaki gibi: titresim + tik sesi
        private void DugmeHissi(int h)
        {
            try
            {
                object inst = tTagger != null ? UyeAl(null, tTagger, "Instance") : null;
                if (inst == null) return;
                object guc = UyeAl(inst, tTagger, "tapHapticStrength");
                object sure = UyeAl(inst, tTagger, "tapHapticDuration");
                float g = guc is float ? (float)guc / 2f : 0.3f, sr = sure is float ? (float)sure : 0.05f;
                var titre = tTagger.GetMethod("StartVibration", new Type[] { typeof(bool), typeof(float), typeof(float) });
                if (titre != null) titre.Invoke(inst, new object[] { h == 0, g, sr });
                Component rig = YerelRig();
                if (rig != null)
                {
                    var ses = rig.GetType().GetMethod("PlayHandTapLocal", new Type[] { typeof(int), typeof(bool), typeof(float) });
                    if (ses != null) ses.Invoke(rig, new object[] { 67, h == 0, 0.05f });
                }
            }
            catch (Exception) { }
        }
        private float eTikZaman;
        private bool eFareOnceki;

        // vurgu rengi menunun temasindan gelir (Settings > THEME)
        private Color EkKirmizi { get { return Temalar[Mathf.Clamp(temaNo, 0, Temalar.Length - 1)].vurgu; } }
        // vurgunun ustundeki yazi: acik renklerde (beyaz, sari, bej...) koyu, digerlerinde beyaz
        private static Color VurguYazi(Color c)
        {
            float l = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            return l > 0.62f ? new Color(0.08f, 0.08f, 0.1f, 1f) : Color.white;
        }
        private Color EkVurguYazi { get { return VurguYazi(EkKirmizi); } }
        private Color EkKoyuKirmizi { get { Color c = Color.Lerp(EkEkran, EkKirmizi, 0.22f); c.a = 1f; return c; } }
        // gorunum: koyu / acik (Settings > THEME > APPEARANCE)
        private bool ekAydinlik;
        private BepInEx.Configuration.ConfigEntry<bool> ekAydinlikCfg;
        private static Color Rk(int r, int g, int b) { return new Color(r / 255f, g / 255f, b / 255f, 1f); }
        private Color EkCerceve { get { return ekAydinlik ? Rk(0xd2, 0xd4, 0xda) : Rk(0x3b, 0x3d, 0x45); } }
        private Color EkEkran   { get { return ekAydinlik ? Rk(0xf4, 0xf5, 0xf7) : Rk(0x18, 0x18, 0x1c); } }
        private Color EkKutu    { get { return ekAydinlik ? Rk(0xff, 0xff, 0xff) : Rk(0x22, 0x22, 0x27); } }
        private Color EkKenar   { get { return ekAydinlik ? Rk(0xdc, 0xdd, 0xe3) : Rk(0x2c, 0x2c, 0x33); } }
        private Color EkKoyu    { get { return ekAydinlik ? Rk(0xe9, 0xea, 0xee) : Rk(0x11, 0x11, 0x14); } }
        private Color EkMetin   { get { return ekAydinlik ? Rk(0x1d, 0x1e, 0x24) : Rk(0xec, 0xec, 0xf2); } }
        private Color EkSoluk   { get { return ekAydinlik ? Rk(0x6b, 0x6d, 0x78) : Rk(0x8b, 0x8b, 0x99); } }
        private Color EkEtiket  { get { return ekAydinlik ? Rk(0x7b, 0x7d, 0x88) : Rk(0x7b, 0x7b, 0x88); } }
        private Color EkNot     { get { return ekAydinlik ? Rk(0x8a, 0x8c, 0x96) : Rk(0x6f, 0x6f, 0x7d); } }
        private Color EkBos     { get { return ekAydinlik ? Rk(0xeb, 0xec, 0xef) : Rk(0x1d, 0x1d, 0x22); } }
        private Color EkCizgi   { get { return ekAydinlik ? Rk(0xe6, 0xe7, 0xeb) : Rk(0x4c, 0x4e, 0x57); } }
        private Color EkGovde   { get { return ekAydinlik ? Rk(0xc4, 0xc6, 0xcd) : Rk(0x30, 0x31, 0x38); } }
        private readonly List<Material[]> govdeTakimlari = new List<Material[]>();
        private static readonly float[] GovdeIsik = { 1.0f, 0.85f, 0.65f, 0.75f };

        private void GovdeRenkGuncelle()
        {
            Color taban = EkGovde;
            foreach (Material[] takim in govdeTakimlari)
            for (int g = 0; g < takim.Length && g < GovdeIsik.Length; g++)
            {
                Material mt = takim[g];
                if (mt == null) continue;
                Color c = new Color(taban.r * GovdeIsik[g], taban.g * GovdeIsik[g], taban.b * GovdeIsik[g], 1f);
                if (mt.HasProperty("_BaseColor")) mt.SetColor("_BaseColor", c);
                if (mt.HasProperty("_Color")) mt.SetColor("_Color", c);
            }
        }

        // yazi stili: 0 orijinal (simdiki), 1 Gorilla Tag'in kendi yazisi, 2 modern (Bahnschrift)
        private int yaziStili;
        private BepInEx.Configuration.ConfigEntry<int> yaziStiliCfg;
        private static readonly string[] YaziStiliAd = { "Original", "Gorilla Tag", "Modern" };
        private Font fontOrijinal, fontGtag, fontModern;
        private bool gtagArandi, modernArandi;

        private void YaziStiliSec(int i)
        {
            yaziStili = i;
            if (yaziStiliCfg != null) yaziStiliCfg.Value = i;
            ekranImza = "";
        }

        private Font EkFontSec()
        {
            if (fontOrijinal == null)
            {
                try { fontOrijinal = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
                if (fontOrijinal == null) { try { fontOrijinal = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { } }
            }
            if (yaziStili == 1)
            {
                if (fontGtag == null && !gtagArandi) { gtagArandi = true; try { fontGtag = GtagFontBul(); } catch (Exception e) { Logger.LogWarning("GT font search error: " + e.Message); } }
                if (fontGtag != null) return fontGtag;
            }
            else if (yaziStili == 2)
            {
                if (fontModern == null && !modernArandi)
                {
                    modernArandi = true;
                    try { fontModern = Font.CreateDynamicFontFromOSFont(new[] { "Bahnschrift", "Segoe UI Semibold", "Segoe UI" }, 32); }
                    catch (Exception e) { Logger.LogWarning("Modern font error: " + e.Message); }
                }
                if (fontModern != null) return fontModern;
            }
            return fontOrijinal;
        }

        // oyunun kendi yazilarinda en cok kullanilan font (TextMesh, UI Text, TextMeshPro)
        private Font GtagFontBul()
        {
            var say = new Dictionary<Font, int>();
            Action<Font> ekle = f =>
            {
                if (f == null) return;
                string ad = f.name.ToLowerInvariant();
                if (ad.Contains("arial") || ad.Contains("legacyruntime")) return;
                int n; say.TryGetValue(f, out n); say[f] = n + 1;
            };
            foreach (TextMesh tm in Resources.FindObjectsOfTypeAll<TextMesh>()) ekle(tm.font);
            foreach (UnityEngine.UI.Text t in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>())
                if (t != null && !t.name.Equals("t")) ekle(t.font);
            Type tTmp = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
            if (tTmp != null)
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(tTmp))
                {
                    object fa = UyeAl(o, tTmp, "font");
                    if (fa == null) continue;
                    ekle(UyeAl(fa, fa.GetType(), "sourceFontFile") as Font);
                }
            Font en = null; int enN = 0;
            foreach (var kv in say) if (kv.Value > enN) { en = kv.Key; enN = kv.Value; }
            var sb = new StringBuilder("Fonts found in game:");
            foreach (var kv in say) sb.Append(" " + kv.Key.name + "(" + kv.Value + ")");
            Logger.LogInfo(sb.ToString());
            Logger.LogInfo("Gorilla Tag font: " + (en != null ? en.name : "not found, using original"));
            return en;
        }

        private void GorunumSec(bool acik)
        {
            ekAydinlik = acik;
            if (ekAydinlikCfg != null) ekAydinlikCfg.Value = acik;
            GovdeRenkGuncelle();
        }

        private void EkranKare()
        {
            bool eksik = false;
            for (int i = 0; i < ekranlar.Length; i++) if (ekranlar[i] == null) eksik = true;
            if (eksik && Time.time >= ekranDeneme)
            {
                ekranDeneme = Time.time + 3f;
                if (El(1) != null)     // oyuncu hazir
                {
                    for (int i = 0; i < ekranlar.Length; i++)
                    {
                        if (ekranlar[i] != null) continue;
                        ekranNo = i; ekranGo = null;
                        EkranOlustur();
                        ekranlar[i] = ekranGo;
                        ekIcerikler[i] = null;
                        ekranImza = "";
                    }
                }
            }
            bool var_ = false;
            for (int i = 0; i < ekranlar.Length; i++) if (ekranlar[i] != null) var_ = true;
            if (!var_) return;

            if (Time.time - ekranImzaZaman > 0.3f)
            {
                ekranImzaZaman = Time.time;
                string imza = EkranImza();
                if (imza != ekranImza) { ekranImza = imza; EkranCiz(); }
            }

            EkranGirdi();
        }

        private string EkranImza()
        {
            return eSekme + "|" + eBayrakSayfa + "|" + eSapSayfa + "|" + eAyarSayfa + "|" + gizli + "|" + (tarafCfg != null ? tarafCfg.Value : 0) + "|" + donus + "|" + kozmetik + "|" +
                secili + "|" + aktifKat + "|" + bayraklar.Count + "|" + sapNo + "|" + (sapOzelTex != null) + "|" + saplamaAcik + "|" + kalite + "|" + detay + "|" + dil + "|" +
                ustX + ustY + altX + altY + "|" + yontem + "|" + menuTus + "|" + saplaYuva[0] + saplaYuva[1] + "|" + yuvaDinle + "|" + (eSilBekle >= 0 && Time.time - eSilZaman < 3f) + "|" + (Time.time - eSifirZaman < 3f) + "|" + katListesi.Count + "|" + temaNo + "|" + ekAydinlik + "|" + alinabilir + "|" + donusMesafe + "|" + SaplamaHizi + "|" + yaziStili + "|" + tirmanAcik + "|" + sandboxAcik + firlatAcik + "|" + eFavori + favoriler.Count + "|" + eGenelSayfa + (eSekme == 0 && eGenelSayfa == 1 ? OyuncuImza() : "");
        }

        private void EkranOlustur()
        {
            try
            {
                if (ekranFont == null) { try { ekranFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { ekranFont = null; } }
                if (ekranFont == null) { try { ekranFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { ekranFont = null; } }
                if (ekranFont == null) { Logger.LogWarning("Wall screen: no font found."); return; }

                // yuvarlak kose sprite (9-slice)
                if (ekranYuvarlak == null) {
                int n = 32; float rad = 10f;
                Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
                t.wrapMode = TextureWrapMode.Clamp;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float cx = Mathf.Clamp(x + 0.5f, rad, n - rad), cy = Mathf.Clamp(y + 0.5f, rad, n - rad);
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                        t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(rad - d + 0.5f)));
                    }
                t.Apply();
                ekranYuvarlak = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(11, 11, 11, 11));
                }

                ekranGo = new GameObject("GorillaFlag_WallScreen_" + ekranNo);
                DontDestroyOnLoad(ekranGo);
                Canvas c = ekranGo.AddComponent<Canvas>();
                c.renderMode = RenderMode.WorldSpace;
                var sc = ekranGo.AddComponent<UnityEngine.UI.CanvasScaler>();
                sc.dynamicPixelsPerUnit = 4f;
                RectTransform rt = ekranGo.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(EkW, EkH);

                // ekran dumduz (dik) durur; duvara girmeyecek kadar one alinir, arkasi duvara kadar dolu
                Vector3 nrm = new Vector3(EkranNormaller[ekranNo].x, 0f, EkranNormaller[ekranNo].z).normalized;
                Quaternion rot = Quaternion.LookRotation(-nrm, Vector3.up);
                float olcek = EkMetre / EkW;
                float ekH = EkH * olcek;
                Vector3 merkez = EkranNoktalar[ekranNo] + rot * Vector3.right * EkranSagaKaydir[ekranNo];   // biraz saga

                // duvar ekran duzleminin ne kadar onune / arkasina cikiyor? 9x9 noktadan isin at
                Transform kok = OyuncuKoku();
                float enCok = -1f, enAz = 1f; int isabet = 0;
                for (int iy = 0; iy < 9; iy++)
                    for (int ix = 0; ix < 9; ix++)
                    {
                        Vector3 p = merkez + rot * new Vector3((ix / 8f - 0.5f) * EkMetre, (iy / 8f - 0.5f) * ekH, 0f);
                        Vector3 bas = p + nrm * 0.6f;
                        RaycastHit[] hits = Physics.RaycastAll(bas, -nrm, 1.2f, ~0, QueryTriggerInteraction.Ignore);
                        float en = float.MaxValue;
                        foreach (RaycastHit h in hits)
                        {
                            if (h.collider == null) continue;
                            if (kok != null && h.collider.transform.IsChildOf(kok)) continue;
                            if (yerelKopya != null && h.collider.transform.IsChildOf(yerelKopya.transform)) continue;
                            if (h.distance < en) en = h.distance;
                        }
                        if (en == float.MaxValue) continue;
                        float cc = 0.6f - en;          // + : duvar duzlemin onunde
                        enCok = Mathf.Max(enCok, cc); enAz = Mathf.Min(enAz, cc); isabet++;
                    }
                if (isabet == 0) { enCok = 0f; enAz = 0f; }
                enCok = Mathf.Clamp(enCok, -0.1f, 0.3f);
                enAz = Mathf.Clamp(enAz, -0.3f, enCok);
                float uzak = enCok + 0.01f + EkranOneAl[ekranNo];                                       // on yuz: duvarin en cikik yerinin 1 cm onu
                float kalin = Mathf.Clamp(uzak - enAz + 0.02f, 0.03f, 0.5f);      // arka: en derin yere kadar (+2 cm duvara gomulu)

                ekranGo.transform.position = merkez + nrm * uzak;
                ekranGo.transform.rotation = rot;
                ekranGo.transform.localScale = Vector3.one * olcek;

                float derinBirim = kalin / olcek;
                if (!GovdeMeshKur(derinBirim))
                {
                // arka govde: ayri canvas -> onden/arkadan bakinca dogru sirada cizilir
                GameObject govde = new GameObject("GorillaFlag_WallScreen_Body");
                govde.transform.SetParent(ekranGo.transform, false);
                Canvas gc = govde.AddComponent<Canvas>();
                gc.renderMode = RenderMode.WorldSpace;
                RectTransform grt = govde.GetComponent<RectTransform>();
                grt.sizeDelta = new Vector2(EkW, EkH);
                grt.localPosition = new Vector3(0f, 0f, derinBirim / 2f);
                // yan yuzler: yuvarlak koseli dikdortgenin cevresi boyunca tek parca paneller (tirtik olmaz)
                Color govdeRenk = new Color32(0x30, 0x31, 0x38, 255);
                float yr = 11f, ya = EkW / 2f, yb = EkH / 2f;
                List<Vector2> cevre = new List<Vector2>();
                Vector2[] koseMerkez = { new Vector2(ya - yr, yb - yr), new Vector2(-ya + yr, yb - yr), new Vector2(-ya + yr, -yb + yr), new Vector2(ya - yr, -yb + yr) };
                for (int ki = 0; ki < 4; ki++)
                    for (int sgm = 0; sgm <= 5; sgm++)
                    {
                        float aci = (ki * 90f + sgm * 18f) * Mathf.Deg2Rad;
                        cevre.Add(koseMerkez[ki] + new Vector2(Mathf.Cos(aci), Mathf.Sin(aci)) * yr);
                    }
                for (int pi = 0; pi < cevre.Count; pi++)
                {
                    Vector2 p0 = cevre[pi], p1 = cevre[(pi + 1) % cevre.Count];
                    Vector2 yon = p1 - p0;
                    float boy = yon.magnitude;
                    if (boy < 0.01f) continue;
                    Vector2 dis = new Vector2(yon.y, -yon.x).normalized;   // disa bakan yon
                    GameObject g = new GameObject("yan");
                    RectTransform r = g.AddComponent<RectTransform>();
                    r.SetParent(grt, false);
                    r.sizeDelta = new Vector2(boy + 0.6f, derinBirim);       // 0.6: koseler arasi bosluk kalmasin
                    Vector2 orta = (p0 + p1) / 2f;
                    r.localPosition = new Vector3(orta.x, orta.y, 0f);
                    r.localRotation = Quaternion.LookRotation(new Vector3(dis.x, dis.y, 0f), Vector3.forward);
                    var im = g.AddComponent<UnityEngine.UI.Image>();
                    float isik = 0.82f + 0.18f * dis.y;                       // ust acik, alt koyu
                    im.color = new Color(govdeRenk.r * isik, govdeRenk.g * isik, govdeRenk.b * isik, 1f);
                    im.raycastTarget = false;
                }
                {
                    // arka yuz
                    GameObject g = new GameObject("arka");
                    RectTransform r = g.AddComponent<RectTransform>();
                    r.SetParent(grt, false);
                    r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
                    r.offsetMin = r.offsetMax = Vector2.zero;
                    r.localPosition = new Vector3(0f, 0f, derinBirim / 2f);
                    var im = g.AddComponent<UnityEngine.UI.Image>();
                    im.sprite = ekranYuvarlak; im.type = UnityEngine.UI.Image.Type.Sliced;
                    im.color = new Color32(0x26, 0x27, 0x2d, 255);
                    im.raycastTarget = false;
                }
                }
                float cikinti = enCok;
                Logger.LogInfo("Wall screen: wall " + enAz.ToString("0.000") + ".." + cikinti.ToString("0.000") + " m, front " + uzak.ToString("0.000") + " m out, body " + kalin.ToString("0.000") + " m deep.");

                ekranImza = "";
                Logger.LogInfo("Wall screen " + ekranNo + " created at " + ekranGo.transform.position);
            }
            catch (Exception e)
            {
                Logger.LogWarning("Wall screen error: " + e.Message);
                if (ekranGo != null) Destroy(ekranGo);
                ekranGo = null;
            }
        }

        // Arka govde: gercek (opak, derinlik yazan) mesh. Yandan bakinca ekran yazilari icinden gorunmez.
        private bool GovdeMeshKur(float derin)
        {
            string[] adlar = { "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Lit", "Unlit/Color", "GorillaTag/UberShader" };
            Shader sh = null;
            foreach (string a in adlar) { sh = Shader.Find(a); if (sh != null) break; }
            if (sh == null) { Logger.LogWarning("Wall screen: no opaque shader, using UI body."); return false; }

            float yr = 11f, ya = EkW / 2f, yb = EkH / 2f;
            List<Vector2> cevre = new List<Vector2>();
            Vector2[] km = { new Vector2(ya - yr, yb - yr), new Vector2(-ya + yr, yb - yr), new Vector2(-ya + yr, -yb + yr), new Vector2(ya - yr, -yb + yr) };
            for (int ki = 0; ki < 4; ki++)
                for (int sg = 0; sg <= 5; sg++)
                {
                    float aci = (ki * 90f + sg * 18f) * Mathf.Deg2Rad;
                    cevre.Add(km[ki] + new Vector2(Mathf.Cos(aci), Mathf.Sin(aci)) * yr);
                }

            List<Vector3> v = new List<Vector3>();
            List<Vector3> nr = new List<Vector3>();
            List<int>[] tri = { new List<int>(), new List<int>(), new List<int>(), new List<int>() };   // 0 ust, 1 yan, 2 alt, 3 arka
            Action<Vector3, Vector3, Vector3, Vector3, Vector3, int> dortgen = (p0, p1, p2, p3, n, grup) =>
            {
                // iki yuzlu: on ve arka yuz ayri vertex (normaller ters)
                int i0 = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                for (int k = 0; k < 4; k++) nr.Add(n);
                tri[grup].AddRange(new[] { i0, i0 + 1, i0 + 2, i0, i0 + 2, i0 + 3 });
                int i1 = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                for (int k = 0; k < 4; k++) nr.Add(-n);
                tri[grup].AddRange(new[] { i1, i1 + 2, i1 + 1, i1, i1 + 3, i1 + 2 });
            };

            for (int pi = 0; pi < cevre.Count; pi++)
            {
                Vector2 p0 = cevre[pi], p1 = cevre[(pi + 1) % cevre.Count];
                Vector2 yon = p1 - p0;
                if (yon.magnitude < 0.01f) continue;
                Vector2 dis = new Vector2(yon.y, -yon.x).normalized;
                int grup = dis.y > 0.7f ? 0 : (dis.y < -0.7f ? 2 : 1);
                dortgen(new Vector3(p0.x, p0.y, 0f), new Vector3(p1.x, p1.y, 0f), new Vector3(p1.x, p1.y, derin), new Vector3(p0.x, p0.y, derin), new Vector3(dis.x, dis.y, 0f), grup);
            }
            // arka yuz: merkezden yelpaze
            for (int pi = 0; pi < cevre.Count; pi++)
            {
                Vector2 p0 = cevre[pi], p1 = cevre[(pi + 1) % cevre.Count];
                int i0 = v.Count;
                v.Add(new Vector3(0f, 0f, derin)); v.Add(new Vector3(p0.x, p0.y, derin)); v.Add(new Vector3(p1.x, p1.y, derin));
                for (int k = 0; k < 3; k++) nr.Add(Vector3.forward);
                tri[3].AddRange(new[] { i0, i0 + 1, i0 + 2, i0, i0 + 2, i0 + 1 });
            }

            Mesh m = new Mesh();
            m.name = "GorillaFlag_WallScreen_Body";
            m.SetVertices(v);
            m.SetNormals(nr);
            m.subMeshCount = 4;
            for (int g = 0; g < 4; g++) m.SetTriangles(tri[g], g);
            m.RecalculateBounds();

            Color taban = EkGovde;
            float[] isik = GovdeIsik;
            Material[] mats = new Material[4];
            for (int g = 0; g < 4; g++)
            {
                Material mt = new Material(sh);
                Color c = new Color(taban.r * isik[g], taban.g * isik[g], taban.b * isik[g], 1f);
                if (mt.HasProperty("_BaseColor")) mt.SetColor("_BaseColor", c);
                if (mt.HasProperty("_Color")) mt.SetColor("_Color", c);
                if (mt.HasProperty("_BaseMap")) mt.SetTexture("_BaseMap", Texture2D.whiteTexture);
                if (mt.HasProperty("_Cull")) mt.SetFloat("_Cull", 0f);
                mats[g] = mt;
            }

            GameObject go = new GameObject("GorillaFlag_WallScreen_Body");
            go.transform.SetParent(ekranGo.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = m;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            govdeTakimlari.Add(mats);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            Logger.LogInfo("Wall screen body: mesh with shader " + sh.name);
            return true;
        }

        // ---------- cizim yardimcilari (tasarim koordinati: sol ust 0,0 - 476x660) ----------
        private RectTransform EkYer(GameObject g, float x, float y, float w, float h)
        {
            RectTransform r = g.GetComponent<RectTransform>();
            if (r == null) r = g.AddComponent<RectTransform>();
            r.SetParent(ekranIcerik, false);
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, -y);
            r.sizeDelta = new Vector2(w, h);
            return r;
        }

        private Sprite ekranDaireSp;
        private void EkDaire(float cx, float cy, float cap, Color c)
        {
            if (ekranDaireSp == null)
            {
                int n = 64;
                Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
                t.wrapMode = TextureWrapMode.Clamp;
                float r = n / 2f;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                        t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d)));
                    }
                t.Apply();
                ekranDaireSp = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            }
            GameObject g = new GameObject("d");
            EkYer(g, cx - cap / 2f, cy - cap / 2f, cap, cap);
            var im = g.AddComponent<UnityEngine.UI.Image>();
            im.sprite = ekranDaireSp;
            im.color = c;
            im.raycastTarget = false;
        }

        private UnityEngine.UI.Image EkKutuCiz(float x, float y, float w, float h, Color c, bool yuvarlak = true)
        {
            GameObject g = new GameObject("b");
            EkYer(g, x, y, w, h);
            var im = g.AddComponent<UnityEngine.UI.Image>();
            if (yuvarlak) { im.sprite = ekranYuvarlak; im.type = UnityEngine.UI.Image.Type.Sliced; }
            im.color = c;
            im.raycastTarget = false;
            return im;
        }

        private void EkKenarli(float x, float y, float w, float h, Color dolgu, Color kenar)
        {
            EkKutuCiz(x, y, w, h, kenar);
            EkKutuCiz(x + 2, y + 2, w - 4, h - 4, dolgu);
        }

        private UnityEngine.UI.Text EkYazi(float x, float y, float w, float h, string s, int boyut, Color c, TextAnchor hiza = TextAnchor.MiddleCenter, bool kalin = true)
        {
            GameObject g = new GameObject("t");
            EkYer(g, x, y, w, h);
            var tx = g.AddComponent<UnityEngine.UI.Text>();
            tx.font = ekranFont;
            // Gorilla Tag yazi tipi ayni boyutta daha kucuk gorunuyor: kucuk yazilari daha cok buyut
            if (yaziStili == 1) boyut += boyut <= 12 ? 4 : (boyut <= 16 ? 3 : 2);
            tx.fontSize = boyut;
            tx.fontStyle = kalin ? FontStyle.Bold : FontStyle.Normal;
            tx.color = c;
            tx.alignment = hiza;
            tx.supportRichText = true;
            tx.horizontalOverflow = HorizontalWrapMode.Wrap;
            tx.verticalOverflow = VerticalWrapMode.Overflow;
            tx.raycastTarget = false;
            tx.text = s;
            return tx;
        }

        private void EkResim(float x, float y, float w, float h, Texture tex, float donusZ = 0f)
        {
            if (tex == null) return;
            GameObject g = new GameObject("i");
            RectTransform r = EkYer(g, x, y, w, h);
            if (donusZ != 0f)
            {
                r.pivot = new Vector2(0.5f, 0.5f);
                r.anchoredPosition = new Vector2(x + w / 2f, -(y + h / 2f));
                r.sizeDelta = new Vector2(h, w);
                r.localRotation = Quaternion.Euler(0f, 0f, donusZ);
            }
            var ri = g.AddComponent<UnityEngine.UI.RawImage>();
            ri.texture = tex;
            ri.raycastTarget = false;
        }

        private void EkTik(float x, float y, float w, float h, Action a)
        {
            eTiklar.Add(new KeyValuePair<Rect, Action>(new Rect(x, y, w, h), a));
        }

        private void EkEtiketYaz(float y, string s)
        {
            EkYazi(36, y, 400, 16, s.ToUpperInvariant(), 11, EkEtiket, TextAnchor.MiddleLeft);
        }

        private void EkDugme(float x, float y, float w, float h, string s, bool on, Action a, int boyut = 15, bool aktif = true)
        {
            if (on) EkKutuCiz(x, y, w, h, EkKirmizi);
            else EkKenarli(x, y, w, h, EkKutu, EkKenar);
            EkYazi(x, y, w, h, s, boyut, on ? EkVurguYazi : EkSoluk);
            if (aktif && a != null) EkTik(x, y, w, h, a);
        }

        private void EkSegment(float y, string[] adlar, int secilen, Action<int> sec, bool aktif = true)
        {
            float g = 408f, ara = 6f, w = (g - ara * (adlar.Length - 1)) / adlar.Length;
            for (int i = 0; i < adlar.Length; i++)
            {
                int k = i;
                EkDugme(34 + i * (w + ara), y, w, 38, adlar[i], i == secilen, () => sec(k), 14, aktif);
            }
        }

        private void EkAnahtar(float y, string ad, bool acik, Action a, float h = 44f, int boyut = 16, bool aktif = true)
        {
            if (acik) EkKutuCiz(34, y, 408, h, EkKirmizi);
            else EkKenarli(34, y, 408, h, EkKutu, EkKenar);
            EkYazi(50, y, 300, h, ad, boyut, acik ? EkVurguYazi : EkMetin, TextAnchor.MiddleLeft);
            EkKutuCiz(372, y + h / 2f - 11, 56, 22, acik ? new Color(0, 0, 0, 0.25f) : EkKenar);
            EkYazi(372, y + h / 2f - 11, 56, 22, acik ? "ON" : "OFF", 11, acik ? EkVurguYazi : EkSoluk);
            if (aktif) EkTik(34, y, 408, h, a);
        }

        private void EkEylem(float y, string ikon, string baslik, string alt, bool tehlike, Action a)
        {
            EkKenarli(34, y, 408, 48, EkKutu, EkKenar);
            EkKutuCiz(46, y + 9, 30, 30, tehlike ? EkKirmizi : EkKenar);
            EkYazi(46, y + 9, 30, 30, ikon, 14, tehlike ? EkVurguYazi : EkMetin);
            EkYazi(88, y + 6, 330, 20, baslik, 15, tehlike ? (Color)new Color32(0xff, 0x6b, 0x7a, 255) : EkMetin, TextAnchor.MiddleLeft);
            EkYazi(88, y + 26, 330, 16, alt, 11, EkNot, TextAnchor.MiddleLeft, false);
            EkTik(34, y, 408, 48, a);
        }

        private void EkSayfalayici(float y, string orta, int sayfa, int toplam, Action<int> git)
        {
            EkKenarli(34, y, 52, 38, EkKutu, EkKenar);
            EkYazi(34, y, 52, 38, "<", 18, sayfa > 0 ? EkMetin : EkKenar);
            if (sayfa > 0) EkTik(34, y, 52, 38, () => git(sayfa - 1));
            EkKenarli(92, y, 292, 38, EkKoyu, EkKenar);
            EkYazi(92, y, 292, 38, orta, 13, EkSoluk);
            EkKenarli(390, y, 52, 38, EkKutu, EkKenar);
            EkYazi(390, y, 52, 38, ">", 18, sayfa < toplam - 1 ? EkMetin : EkKenar);
            if (sayfa < toplam - 1) EkTik(390, y, 52, 38, () => git(sayfa + 1));
        }

        // ---------- sayfalar ----------
        private void EkranCiz()
        {
            for (int i = 0; i < ekranlar.Length; i++)
            {
                if (ekranlar[i] == null) continue;
                ekranGo = ekranlar[i];
                ekranIcerik = ekIcerikler[i];
                EkranCizTek();
                ekIcerikler[i] = ekranIcerik;
            }
        }

        private void EkranCizTek()
        {
            if (ekranGo == null) return;
            Font fs = EkFontSec();
            if (fs != null) ekranFont = fs;
            if (ekranIcerik != null) Destroy(ekranIcerik.gameObject);
            eTiklar.Clear();

            GameObject ic = new GameObject("Icerik");
            ekranIcerik = ic.AddComponent<RectTransform>();
            ekranIcerik.SetParent(ekranGo.transform, false);
            ekranIcerik.anchorMin = Vector2.zero; ekranIcerik.anchorMax = Vector2.one;
            ekranIcerik.offsetMin = ekranIcerik.offsetMax = Vector2.zero;

            // cerceve + ekran
            EkKutuCiz(0, 0, EkW, EkH, EkCerceve);
            EkKutuCiz(4, 3, EkW - 8, 3, EkCizgi, false);
            EkKutuCiz(16, 16, EkW - 32, EkH - 32, EkEkran);

            // baslik
            EkYazi(34, 30, 300, 36, "Gorilla<color=#" + ColorUtility.ToHtmlStringRGB(EkKirmizi) + ">Flag</color>", 26, EkMetin, TextAnchor.MiddleLeft);
            EkKenarli(352, 38, 90, 22, EkEkran, EkKenar);
            EkYazi(352, 38, 90, 22, "v1.0", 11, EkSoluk);

            // sekmeler
            string[] ad = { "General", "Flags", "Pole", "Settings" };
            float tw = (408f - 18f) / 4f;
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                EkDugme(34 + i * (tw + 6f), 76, tw, 36, Dil(ad[i]), eSekme == i, () => { eSekme = k; }, 13);
            }

            try
            {
                if (eSekme == 0) EkGenel();
                else if (eSekme == 1) EkBayraklar();
                else if (eSekme == 2) EkSap();
                else EkAyarlar();
            }
            catch (Exception e) { Logger.LogWarning("Wall screen page error: " + e.Message); }
        }

        private int eGenelSayfa = 0;

        private void EkGenel()
        {
            if (eGenelSayfa == 1) { EkOyuncular(); return; }
            EkAnahtar(122, Dil("Private"), gizli, () => GizliSec(!gizli), 50, 18);
            EkYazi(36, 174, 404, 18, Dil("Only you see it. You don't need to own the flag."), 11, EkNot, TextAnchor.MiddleLeft, false);
            bool ak = gizli;

            EkEtiketYaz(198, Dil("SIDE"));
            int simdi = tarafCfg != null ? tarafCfg.Value : 0;
            EkSegment(216, new[] { Dil("Left"), Dil("Right") }, simdi == 1 ? 0 : 1, i =>
            {
                if (tarafCfg == null) return;
                int d = i == 0 ? 1 : 0;
                tarafCfg.Value = d; sirtSol = d == 1;
                if (!tutuluyor) SirtaKoy();
            }, ak);

            EkEtiketYaz(264, Dil("ROTATION"));
            EkKenarli(34, 282, 408, 44, EkKutu, EkKenar);
            EkKutuCiz(44, 289, 30, 30, EkKenar); EkYazi(44, 289, 30, 30, "-", 18, EkMetin);
            EkKutuCiz(338, 289, 30, 30, EkKenar); EkYazi(338, 289, 30, 30, "+", 18, EkMetin);
            float tx0 = 84, tw0 = 244;
            EkKutuCiz(tx0, 300, tw0, 8, EkKenar);
            float f = donus / 360f;
            if (f > 0.01f) EkKutuCiz(tx0, 300, tw0 * f, 8, EkKirmizi);
            EkKutuCiz(tx0 + tw0 * f - 11, 293, 22, 22, EkKirmizi);
            EkKutuCiz(tx0 + tw0 * f - 8, 296, 16, 16, EkMetin);
            EkYazi(372, 282, 64, 44, donus + "°", 15, EkMetin);
            if (ak)
            {
                EkTik(40, 282, 40, 44, () => { donus = ((donus - 15) % 360 + 360) % 360; donusDirty = true; });
                EkTik(334, 282, 40, 44, () => { donus = (donus + 15) % 360; donusDirty = true; });
                for (int s = 0; s < 24; s++)
                {
                    int deger = s * 15;
                    EkTik(tx0 + tw0 * (s / 24f), 282, tw0 / 24f, 44, () => { donus = deger; donusDirty = true; });
                }
            }

            EkEtiketYaz(336, Dil("QUICK ANGLES"));
            int[] pre = { 0, 90, 180, 270 };
            EkSegment(354, new[] { "0°", "90°", "180°", "270°" }, Array.IndexOf(pre, donus), i => { donus = pre[i]; donusDirty = true; }, ak);

            EkEtiketYaz(402, Dil("VARIANT"));
            EkKenarli(34, 420, 408, 44, EkKutu, EkKenar);
            EkKutuCiz(44, 427, 30, 30, EkKenar); EkYazi(44, 427, 30, 30, "<", 16, EkMetin);
            EkKutuCiz(402, 427, 30, 30, EkKenar); EkYazi(402, 427, 30, 30, ">", 16, EkMetin);
            EkYazi(80, 420, 316, 44, Dil(KozAdlari[kozmetik]), 17, EkMetin);
            if (ak)
            {
                int sira = Math.Max(0, Array.IndexOf(KozSira, kozmetik));
                EkTik(34, 420, 120, 44, () => KozmetikSec(KozSira[(sira + KozSira.Length - 1) % KozSira.Length]));
                EkTik(154, 420, 288, 44, () => KozmetikSec(KozSira[(sira + 1) % KozSira.Length]));
            }
            EkYazi(36, 468, 404, 18, kozmetik == 3 ? Dil("The real flag cosmetics in game are not changed.") : Dil("Everyone sees it. You must own and wear this flag in game."), 11, EkNot, TextAnchor.MiddleLeft, false);

            if (!ak) EkKutuCiz(30, 194, 416, 296, new Color(EkEkran.r, EkEkran.g, EkEkran.b, 0.6f), false);   // kapali: soluk

            EkSayfalayici(540, Dil("Players") + "  ·  1 / 2", 0, 2, p => eGenelSayfa = p);


            string sec = secili >= 0 && secili < isimler.Count ? Baslikla(isimler[secili]) + "  ·  " + KatGorunen(kategoriler[secili]) : Dil("Original flag");
            EkKenarli(34, 588, 408, 38, EkKoyu, EkKenar);
            EkYazi(34, 588, 408, 38, sec, 13, EkSoluk);
        }


        // ---------- General 2. sayfa: odadaki mod kullananlar (liderlik tablosu gibi) ----------
        private int YerelDurumKod()
        {
            if (!gizli || yerelKopya == null) return 9;
            if (tasiyan >= 0) return 5;
            if (sapli) return yerde ? 4 : 3;
            if (tutuluyor) return 1;
            return 0;
        }

        private static string DurumAd(int d)
        {
            switch (d)
            {
                case 0: return "On back";
                case 1: case 2: return "In hand";
                case 3: return "Planted";
                case 4: return "Dropped";
                case 5: return "Carried";
                default: return "No flag";
            }
        }

        private static string OyuncuAdi(int aktor)
        {
            try
            {
                foreach (Photon.Realtime.Player p in Photon.Pun.PhotonNetwork.PlayerList)
                    if (p != null && p.ActorNumber == aktor && !string.IsNullOrEmpty(p.NickName)) return p.NickName;
            }
            catch (Exception) { }
            return "#" + aktor;
        }

        private static Color OyuncuRengi(Component rig)
        {
            try
            {
                if (rig != null)
                {
                    object c = UyeAl(rig, rig.GetType(), "playerColor");
                    if (c is Color) { Color k = (Color)c; k.a = 1f; return k; }
                }
            }
            catch (Exception) { }
            return new Color(0.55f, 0.55f, 0.6f, 1f);
        }

        private string OyuncuImza()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(OzelOdada()).Append(YerelDurumKod()).Append(secili).Append(sapBoyM);
            foreach (var kv in uzaklar)
            {
                // sadece listede gorunenler (bayrak, durum, sap boyu): el pozisyonu degisince ekran yeniden cizilmesin
                string[] p = (kv.Value.veri ?? "").Split('|');
                sb.Append('#').Append(kv.Key).Append(p.Length > 1 ? p[1] : "").Append(p.Length > 5 ? p[5] : "").Append(p.Length > 12 ? p[12] : "");
            }
            return sb.ToString();
        }

        private void EkOyuncular()
        {
            bool ozel = OzelOdada();
            EkEtiketYaz(122, Dil("MOD USERS"));
            float y = 142;
            int sira = 0;
            Action<string, Color, Texture, int, float, bool> satir = (ad, renk, tx, dr, sapM, ben) =>
            {
                sira++;
                if (ben) EkKenarli(34, y, 408, 36, EkKoyuKirmizi, EkKirmizi);
                else EkKenarli(34, y, 408, 36, EkKutu, EkKenar);
                EkYazi(38, y, 26, 36, sira.ToString(), 13, EkSoluk);
                EkKutuCiz(66, y + 9, 18, 18, renk);
                EkYazi(92, y, 150, 36, ad + (ben ? "  <size=10>(" + Dil("you") + ")</size>" : ""), 13, EkMetin, TextAnchor.MiddleLeft);
                if (tx != null) EkResim(246, y + 6, 36, 24, tx);
                else EkKutuCiz(246, y + 6, 36, 24, EkKenar);
                EkYazi(288, y, 92, 36, Dil(DurumAd(dr)), 11, EkSoluk);
                EkYazi(380, y, 58, 36, sapM.ToString("0.0", CultureInfo.InvariantCulture) + " m", 12, EkMetin);
                y += 39;
            };

            string benAd = "";
            try { if (Photon.Pun.PhotonNetwork.LocalPlayer != null) benAd = Photon.Pun.PhotonNetwork.LocalPlayer.NickName; } catch (Exception) { }
            if (string.IsNullOrEmpty(benAd)) benAd = Dil("you");
            Texture benTex = secili >= 0 && secili < bayraklar.Count ? bayraklar[secili] : null;
            satir(benAd, OyuncuRengi(YerelRig()), benTex, YerelDurumKod(), sapBoyM, true);

            List<int> anahtarlar = new List<int>(uzaklar.Keys);
            anahtarlar.Sort();
            int digerSay = 0;
            foreach (int a in anahtarlar)
            {
                if (sira >= 10) break;
                Uzak u = uzaklar[a];
                string[] p = (u.veri ?? "").Split('|');
                if (p.Length < 7 || p[0] != "1") continue;
                int idx = BayrakBul(p[1]);
                int dur = 0; int.TryParse(p[5], out dur);
                float ek = 0f;
                if (p.Length >= 13) float.TryParse(p[12], NumberStyles.Float, CultureInfo.InvariantCulture, out ek);
                Texture tex = idx >= 0 && idx < bayraklar.Count ? bayraklar[idx] : null;
                satir(OyuncuAdi(a), OyuncuRengi(u.rig), tex, dur, (SapOrjMesh + Mathf.Max(0f, ek)) * BirimM, false);
                digerSay++;
            }
            if (digerSay == 0)
                EkYazi(34, y + 6, 408, 40, ozel ? Dil("No one else in the room has the mod.") : Dil("Mod users are shown in private rooms."), 12, EkNot);

            EkSayfalayici(540, Dil("Players") + "  ·  2 / 2", 1, 2, p => eGenelSayfa = p);
            EkKenarli(34, 588, 408, 38, EkKoyu, EkKenar);
            EkYazi(34, 588, 408, 38, Dil("Mod users") + ": " + (digerSay + 1), 13, EkSoluk);
        }

        // ---------- favoriler: kartin kosesindeki yildiza bas, Favorites sekmesinde toplanir ----------
        private readonly HashSet<string> favoriler = new HashSet<string>();
        private BepInEx.Configuration.ConfigEntry<string> favCfg;
        private bool eFavori;
        private Texture2D yildizDolu, yildizBos;
        private string FavAnahtar(int i) { return BayrakAnahtari(i); }
        private bool FavMi(int i) { return favoriler.Contains(FavAnahtar(i)); }
        private void FavDegistir(int i)
        {
            string a = FavAnahtar(i);
            if (string.IsNullOrEmpty(a)) return;
            if (!favoriler.Remove(a)) favoriler.Add(a);
            if (favCfg != null) favCfg.Value = string.Join("|", new List<string>(favoriler).ToArray());
        }
        private static Texture2D YildizDoku(bool dolu)
        {
            const int n = 32;
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
            Vector2[] k = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float a = Mathf.PI / 2f + i * Mathf.PI / 5f, r = (i % 2 == 0 ? 0.48f : 0.2f) * n;
                k[i] = new Vector2(n / 2f + Mathf.Cos(a) * r, n / 2f + Mathf.Sin(a) * r - 1f);
            }
            Color renk = dolu ? new Color(1f, 0.8f, 0.15f, 1f) : new Color(0.62f, 0.62f, 0.68f, 1f);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    bool ic = false;
                    for (int i = 0, j = 9; i < 10; j = i++)
                        if ((k[i].y > p.y) != (k[j].y > p.y) && p.x < (k[j].x - k[i].x) * (p.y - k[i].y) / (k[j].y - k[i].y) + k[i].x) ic = !ic;
                    // bos yildiz: sadece kenar cizgisi
                    float kenar = float.MaxValue;
                    for (int i = 0, j = 9; i < 10; j = i++)
                    {
                        Vector2 ab = k[i] - k[j]; float tt = Mathf.Clamp01(Vector2.Dot(p - k[j], ab) / ab.sqrMagnitude);
                        kenar = Mathf.Min(kenar, Vector2.Distance(p, k[j] + ab * tt));
                    }
                    float alfa = dolu ? (ic ? 1f : Mathf.Clamp01(1.2f - kenar)) : Mathf.Clamp01(2.2f - kenar);
                    t.SetPixel(x, y, new Color(renk.r, renk.g, renk.b, alfa));
                }
            t.Apply();
            return t;
        }

        private void EkBayraklar()
        {
            if (aktifKat >= katListesi.Count) aktifKat = 0;
            // kategoriler: alti cizgili yazi (sekmelerden farkli); en basta Favorites
            int kn = Math.Min(katListesi.Count, 5);
            float cw = 408f / kn;
            EkKutuCiz(34, 164, 408, 2, EkKenar, false);
            for (int p = 0; p < kn; p++)
            {
                int k = KatSira(p);
                bool on = !eFavori && k == aktifKat;
                float x = 34 + p * cw;
                EkYazi(x, 120, cw, 24, KatGorunen(k), 13, on ? EkMetin : EkEtiket);
                EkYazi(x, 142, cw, 16, KatSayisi(k).ToString(), 10, on ? EkKirmizi : EkNot);
                if (on) EkKutuCiz(x + cw * 0.15f, 162, cw * 0.7f, 4, EkKirmizi);
                EkTik(x, 118, cw, 46, () => { eFavori = false; aktifKat = k; eBayrakSayfa = 0; });
            }
            if (yildizDolu == null) yildizDolu = YildizDoku(true);
            if (yildizBos == null) yildizBos = YildizDoku(false);

            List<int> goster = new List<int>();
            HashSet<string> favGorulen = new HashSet<string>();
            eFavori = false;
            for (int i = 0; i < bayraklar.Count && i < kategoriler.Count; i++) if (kategoriler[i] == aktifKat && FavMi(i)) goster.Add(i);    // favoriler en basta
            for (int i = 0; i < bayraklar.Count && i < kategoriler.Count; i++) if (kategoriler[i] == aktifKat && !FavMi(i)) goster.Add(i);
            bool arti = !eFavori && !HazirKat(aktifKat);
            int adet = goster.Count + (arti ? 1 : 0);
            int toplam = Math.Max(1, (adet + 19) / 20);
            if (eBayrakSayfa >= toplam) eBayrakSayfa = toplam - 1;

            float gw = (408f - 18f) / 4f, gh = 70f;
            for (int s = 0; s < 20; s++)
            {
                int n = eBayrakSayfa * 20 + s;
                float x = 34 + (s % 4) * (gw + 6f), y = 176 + (s / 4) * (gh + 6f);
                if (n >= adet) { EkKutuCiz(x, y, gw, gh, EkBos); continue; }
                if (arti && n == 0)
                {
                    EkKenarli(x, y, gw, gh, EkEkran, EkSoluk);
                    EkKutuCiz(x + gw / 2 - 15, y + 10, 30, 30, EkKirmizi);
                    EkYazi(x + gw / 2 - 15, y + 10, 30, 30, "+", 18, EkVurguYazi);
                    EkYazi(x, y + 44, gw, 20, Dil("Add image"), 11, EkMetin);
                    EkTik(x, y, gw, gh, () => { resimEkle = true; });
                    continue;
                }
                int i = goster[n - (arti ? 1 : 0)];
                bool on = i == secili;
                if (on) EkKenarli(x, y, gw, gh, EkKoyuKirmizi, EkKirmizi);
                else EkKenarli(x, y, gw, gh, EkKutu, EkKenar);
                if (i < thumbs.Count) EkResim(x + gw / 2 - 26, y + 9, 52, 32, thumbs[i]);
                EkYazi(x + 3, y + 45, gw - 6, 20, Baslikla(isimler[i]), 11, on ? EkMetin : EkSoluk);
                int ii = i;
                EkTik(x, y, gw, gh, () => { eSilBekle = -1; Sec(ii); });
                // favori yildizi (kartin sag ust kosesi; once bu yakalar)
                EkResim(x + gw - 21, y + 4, 17, 17, FavMi(i) ? yildizDolu : yildizBos);
                EkTik(x + gw - 28, y, 28, 28, () => FavDegistir(ii));
            }
            if (eFavori && goster.Count == 0)
                EkYazi(34, 300, 408, 40, Dil("Tap the star on a flag to add it here."), 12, EkNot);

            EkSayfalayici(558, Dil("Page") + " " + (eBayrakSayfa + 1) + " / " + toplam, eBayrakSayfa, toplam, p => eBayrakSayfa = p);

            if (arti)
            {
                bool silinebilir = secili >= 0 && secili < kategoriler.Count && !HazirKat(kategoriler[secili]);
                bool bekliyor = eSilBekle == secili && Time.time - eSilZaman < 3f;
                EkKenarli(34, 600, 408, 28, bekliyor ? EkKirmizi : EkKoyu, EkKenar);
                EkYazi(34, 600, 408, 28, bekliyor ? Dil("Click again to delete") : Dil("Delete selected"), 12, silinebilir ? (Color)new Color32(0xff, 0x6b, 0x7a, 255) : EkKenar);
                if (bekliyor) EkKutuCiz(34, 600, 408, 28, new Color(0, 0, 0, 0), false);
                if (silinebilir) EkTik(34, 600, 408, 28, () =>
                {
                    if (eSilBekle == secili && Time.time - eSilZaman < 3f) { int sl = secili; eSilBekle = -1; ResimSil(sl); }
                    else { eSilBekle = secili; eSilZaman = Time.time; }
                });
            }
            else
            {
                EkKenarli(34, 600, 408, 28, EkKoyu, EkKenar);
                EkYazi(34, 600, 408, 28, Dil("Original flag"), 12, EkMetin);
                EkTik(34, 600, 408, 28, () => Orijinale());
            }
        }

        private void EkSap()
        {
            int[][] gruplar = {
                new int[] { 0, 1, 2, 3, 16, 17, 18, 19 },
                new int[] { 4, 5, 6, 7, 20, 21, 22, 23 },
                new int[] { 8, 9, 10, 11, 24, 25, 26, 27, 28, 29 },
                new int[] { 12, 13, 14, 30, 31, 32, 33, 34, 35 } };
            int toplam = 3;
            if (eSapSayfa >= toplam) eSapSayfa = toplam - 1;
            float bw = (408f - 18f) / 4f;
            float y = 122;
            for (int g = 0; g < gruplar.Length; g++)
            {
                EkEtiketYaz(y, Dil(SapGrup[g]));
                y += 20;
                for (int s = 0; s < 4; s++)
                {
                    int idx = eSapSayfa * 4 + s;
                    float x = 34 + s * (bw + 6f);
                    if (idx >= gruplar[g].Length) { EkKutuCiz(x, y, bw, 58, EkBos); continue; }
                    int no = gruplar[g][idx];
                    bool on = no == sapNo;
                    if (on) EkKenarli(x, y, bw, 58, EkKoyuKirmizi, EkKirmizi);
                    else EkKenarli(x, y, bw, 58, EkKutu, EkKenar);
                    EkResim(x + bw / 2 - 31, y + 12, 62, 12, SapIkon(no), 90f);
                    EkYazi(x + 2, y + 32, bw - 4, 20, Dil(SapAdlari[no]), 11, on ? EkMetin : EkSoluk);
                    int nn = no;
                    EkTik(x, y, bw, 58, () => SapSec(nn));
                }
                y += 66;
            }
            EkSayfalayici(476, Dil("More") + "  ·  " + (eSapSayfa + 1) + " / " + toplam, eSapSayfa, toplam, p => eSapSayfa = p);
            bool ozelOn = sapNo == SapOzel;
            EkEylem(522, "+", Dil("Custom PNG"), sapOzelTex != null ? (ozelOn ? Dil("Selected. Click to pick a new image") : Dil("Use your own image for the pole")) : Dil("Use your own image for the pole"), false, () =>
            {
                if (sapOzelTex != null && !ozelOn) SapSec(SapOzel);
                else { sapDosyaModu = true; resimEkle = true; }
            });
            EkEylem(576, "X", Dil("Original pole"), Dil("Back to the default wood pole"), false, () => SapSec(0));
        }

        private void EkAyarlar()
        {
            // tum ayarlar sirayla; sayfaya sigmayan bolum bir sonraki sayfadan devam eder; ACTIONS en sonda
            var bolumler = new List<KeyValuePair<float, Action<float>>>();
            Action<float, Action<float>> ekle = (h, ciz) => bolumler.Add(new KeyValuePair<float, Action<float>>(h, ciz));

            // sira: bayrakla yapilanlar (saplama, firlatma, geri donus, tirmanma, paylasim, GF Sandbox), sonra bayragin gorunusu, sonra menunun gorunusu
            ekle(72, y =>
            {
                EkEtiketYaz(y, Dil("PLANT FLAG"));
                EkAnahtar(y + 18, Dil("Plant flag"), saplamaAcik, () => SaplamaSec(!saplamaAcik));
            });
            ekle(66, y =>
            {
                EkEtiketYaz(y, Dil("PLANT STRENGTH"));
                string[] sh = new string[SaplamaHizAd.Length];
                for (int i = 0; i < sh.Length; i++) sh[i] = Dil(SaplamaHizAd[i]);
                EkSegment(y + 18, sh, Array.IndexOf(SaplamaHizlari, SaplamaHizi), i => SaplamaHiziSec(SaplamaHizlari[i]));
            });
            ekle(122, y =>
            {
                EkEtiketYaz(y, Dil("KEYBINDS"));
                for (int k = 0; k < 2; k++)
                {
                    float ry = y + 18 + k * 50;
                    bool dinliyor = yuvaDinle == k;
                    EkKenarli(34, ry, 408, 44, EkKutu, EkKenar);
                    EkYazi(50, ry, 220, 44, Dil(k == 0 ? "Plant button 1" : "Plant button 2"), 15, EkMetin, TextAnchor.MiddleLeft);
                    if (dinliyor) EkKutuCiz(270, ry + 6, 160, 32, EkKirmizi);
                    else EkKenarli(330, ry + 6, 100, 32, EkKoyu, EkKenar);
                    if (dinliyor) EkYazi(270, ry + 6, 160, 32, Dil("Press a button..."), 12, EkVurguYazi);
                    else EkYazi(330, ry + 6, 100, 32, KolTusAdlari[saplaYuva[k]], 14, EkMetin);
                    int yy = k;
                    EkTik(34, ry, 408, 44, () => YuvaDinleBasla(yy));
                }
                if (!saplamaAcik)
                {
                    // saplama kapali: tuslar calismaz, soluk goster
                    EkKutuCiz(30, y + 16, 416, 100, new Color(EkEkran.r, EkEkran.g, EkEkran.b, 0.6f), false);
                    EkYazi(36, y + 2, 404, 14, Dil("Plant flag is off. Pick a button to turn it on."), 10, EkKirmizi, TextAnchor.MiddleRight, false);
                }
            });
            ekle(92, y =>
            {
                EkEtiketYaz(y, Dil("THROW"));
                EkAnahtar(y + 18, Dil("Throw & catch flag"), firlatAcik, () => FirlatSec(!firlatAcik));
                EkYazi(36, y + 64, 404, 18, Dil("Swing and let go to throw the flag, grab it in the air to catch."), 11, EkNot, TextAnchor.MiddleLeft, false);
            });
            ekle(86, y =>
            {
                EkEtiketYaz(y, Dil("RETURN TO BACK AFTER"));
                string[] dm = new string[DonusMesafeAd.Length];
                for (int i = 0; i < dm.Length; i++) dm[i] = Dil(DonusMesafeAd[i]);
                EkSegment(y + 18, dm, Array.IndexOf(DonusMesafeleri, donusMesafe), i => DonusMesafeSec(DonusMesafeleri[i]));
                EkYazi(36, y + 58, 404, 18, Dil("The flag returns to your back past this distance."), 11, EkNot, TextAnchor.MiddleLeft, false);
            });
            ekle(92, y =>
            {
                EkEtiketYaz(y, Dil("CLIMB"));
                EkAnahtar(y + 18, Dil("Climbable flag"), tirmanAcik, () => TirmanSec(!tirmanAcik));
                EkYazi(36, y + 64, 404, 18, Dil("In modded lobbies planted flags are solid: you can climb them."), 11, EkNot, TextAnchor.MiddleLeft, false);
            });
            ekle(92, y =>
            {
                EkEtiketYaz(y, Dil("SHARING"));
                EkAnahtar(y + 18, Dil("Others can take my flag"), alinabilir, () => AlinabilirSec(!alinabilir));
                EkYazi(36, y + 64, 404, 18, Dil("Mod users can carry, drop and replant your planted flag."), 11, EkNot, TextAnchor.MiddleLeft, false);
            });
            ekle(92, y =>
            {
                EkEtiketYaz(y, "GF SANDBOX");
                EkAnahtar(y + 18, Dil("Pole button"), sandboxAcik, () => SandboxSec(!sandboxAcik));
                EkYazi(36, y + 64, 404, 18, Dil("Small button on the pole that opens GF Sandbox (pole, flag size, base)."), 11, EkNot, TextAnchor.MiddleLeft, false);
            });
            ekle(208, y =>
            {
                EkEtiketYaz(y, Dil("FLAG SIDES"));
                EkAnahtar(y + 18, Dil("Side 1: mirror"), ustX, () => { ustX = !ustX; Yenile(); }, 42, 15);
                EkAnahtar(y + 64, Dil("Side 1: upside down"), ustY, () => { ustY = !ustY; Yenile(); }, 42, 15);
                EkAnahtar(y + 110, Dil("Side 2: mirror"), altX, () => { altX = !altX; Yenile(); }, 42, 15);
                EkAnahtar(y + 156, Dil("Side 2: upside down"), altY, () => { altY = !altY; Yenile(); }, 42, 15);
            });
            ekle(66, y =>
            {
                EkEtiketYaz(y, Dil("APPLY METHOD"));
                EkSegment(y + 18, new[] { "Array", "Keyword", "Shader" }, yontem, i => { yontem = i; Yenile(); });
            });
            ekle(66, y =>
            {
                EkEtiketYaz(y, Dil("TEXTURE QUALITY"));
                string[] ka = new string[KaliteAdlari.Length];
                for (int i = 0; i < ka.Length; i++) ka[i] = Dil(KaliteAdlari[i]);
                EkSegment(y + 18, ka, kalite, i => KaliteSec(i));
            });
            ekle(66, y =>
            {
                EkEtiketYaz(y, Dil("FLAG DETAIL"));
                string[] da = new string[DetayDegerleri.Length];
                for (int i = 0; i < da.Length; i++) da[i] = DetayDegerleri[i].ToString();
                EkSegment(y + 18, da, Array.IndexOf(DetayDegerleri, detay), i => DetaySec(DetayDegerleri[i]));
            });
            ekle(66, y =>
            {
                EkEtiketYaz(y, Dil("APPEARANCE"));
                EkSegment(y + 18, new[] { Dil("Dark"), Dil("Light") }, ekAydinlik ? 1 : 0, i => GorunumSec(i == 1));
            });
            int renkSatir = (Temalar.Length + 6) / 7;
            ekle(18 + renkSatir * 56 + 6, y =>
            {
                EkEtiketYaz(y, Dil("COLOR"));
                float ara = 408f / 7f;
                for (int i = 0; i < Temalar.Length; i++)
                {
                    float cx = 34 + (i % 7) * ara + ara / 2f, cy = y + 18 + 28 + (i / 7) * 56f;
                    if (i == temaNo) EkDaire(cx, cy, 54, EkMetin);
                    EkDaire(cx, cy, 46, Temalar[i].vurgu);
                    Color pc = Temalar[i].panel; pc.a = 1f;
                    EkDaire(cx, cy, 20, pc);
                    int k = i;
                    EkTik(cx - ara / 2f, cy - 28, ara, 56, () => { temaNo = k; if (temaCfg != null) temaCfg.Value = k; stilGecersiz = true; });
                }
            });
            ekle(66, y =>
            {
                EkEtiketYaz(y, Dil("FONT"));
                string[] ys = new string[YaziStiliAd.Length];
                for (int i = 0; i < ys.Length; i++) ys[i] = Dil(YaziStiliAd[i]);
                EkSegment(y + 18, ys, yaziStili, i => YaziStiliSec(i));
            });
            ekle(66, y =>
            {
                EkEtiketYaz(y, Dil("LANGUAGE"));
                EkSegment(y + 18, new[] { "EN", "TR", "ES", "RU" }, dil, i => { dil = i; if (dilCfg != null) dilCfg.Value = i; });
            });
            // islemler: iki dugme yan yana (menunun gorunumu sayfasinin altina sigar)
            ekle(54, y =>
            {
                float w = (408f - 8f) / 2f;
                EkKenarli(34, y, w, 44, EkKutu, EkKenar);
                EkYazi(34, y, w, 44, Dil("Reload flags"), 14, EkMetin);
                EkTik(34, y, w, 44, () => { PngleriYukle(); Yenile(); });
                bool bekle = Time.time - eSifirZaman < 3f;
                float x2 = 34 + w + 8f;
                if (bekle) EkKutuCiz(x2, y, w, 44, EkKirmizi);
                else EkKenarli(x2, y, w, 44, EkKutu, EkKenar);
                EkYazi(x2, y, w, 44, bekle ? Dil("Click again to reset") : Dil("Reset all settings"), 14, bekle ? EkVurguYazi : (Color)new Color32(0xff, 0x6b, 0x7a, 255));
                EkTik(x2, y, w, 44, () =>
                {
                    if (Time.time - eSifirZaman < 3f) { eSifirZaman = -10f; HepsiniSifirla(); }
                    else eSifirZaman = Time.time;
                });
            });

            // sayfalara diz
            const float bas = 122f, son = 578f;
            List<int> sayfaBas = new List<int> { 0 };
            float doluluk = 0f;
            for (int i = 0; i < bolumler.Count; i++)
            {
                if (doluluk > 0f && doluluk + bolumler[i].Key > son - bas) { sayfaBas.Add(i); doluluk = 0f; }
                doluluk += bolumler[i].Key;
            }
            int toplam = sayfaBas.Count;
            if (eAyarSayfa >= toplam) eAyarSayfa = toplam - 1;
            if (eAyarSayfa < 0) eAyarSayfa = 0;
            int ilk = sayfaBas[eAyarSayfa], sonr = eAyarSayfa + 1 < toplam ? sayfaBas[eAyarSayfa + 1] : bolumler.Count;
            float yy0 = bas;
            for (int i = ilk; i < sonr; i++) { bolumler[i].Value(yy0); yy0 += bolumler[i].Key; }

            EkSayfalayici(588, Dil("Settings") + "  ·  " + (eAyarSayfa + 1) + " / " + toplam, eAyarSayfa, toplam, p => eAyarSayfa = p);
        }

        // ---------- dokunma (VR parmak) ve fare (WalkSim) ----------
        private void EkranGirdi()
        {
            bool fare = Application.isFocused && (GetAsyncKeyState(0x01) & 0x8000) != 0;
            bool fareTik = fare && !eFareOnceki && !menuAcik;
            eFareOnceki = fare;
            for (int i = 0; i < ekranlar.Length; i++)
            {
                if (ekranlar[i] == null) continue;
                EkranGirdiTek(ekranlar[i].transform, i, fareTik);
            }
        }

        private void EkranGirdiTek(Transform et, int no, bool fareTik)
        {
            Camera kc = Camera.main;
            if (kc != null && (kc.transform.position - et.position).sqrMagnitude > 25f) return;   // 5 m'den uzaktaysan bakma
            float olcek = et.lossyScale.x;

            // VR: oyundaki dugmeler gibi isaret ya da orta parmak ucuyla bas (parmak ekranin onunden gelip yuzeye degince)
            for (int k = 0; k < 4; k++)
            {
                Vector3 pp;
                if (!BasanNokta(k, out pp)) continue;
                int h = k % 2;
                Vector3 lp = et.InverseTransformPoint(pp);
                float derin = lp.z * olcek;            // + : duvarin icine dogru, - : ekranin onunde
                bool icinde = Mathf.Abs(lp.x) < EkW / 2f && Mathf.Abs(lp.y) < EkH / 2f;
                int bi = no * 4 + k;
                if (derin < -0.035f || !icinde) eHazir[bi] = true;            // parmak ekranin onunde: basmaya hazir
                if (eBasili[bi])
                {
                    if (derin < -0.045f || !icinde) eBasili[bi] = false;       // parmak geri cekildi
                }
                else if (eHazir[bi] && icinde && derin > -0.012f && derin < 0.06f)
                {
                    eBasili[bi] = true;
                    eHazir[bi] = false;
                    if (EkranTikla(lp)) DugmeHissi(h);
                }
            }

            // WalkSim / PC: sol tik, ekranin ortasindan (bakilan yer)
            if (fareTik)
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    Ray r = new Ray(cam.transform.position, cam.transform.forward);
                    Plane pl = new Plane(-et.forward, et.position);
                    float d;
                    if (pl.Raycast(r, out d) && d < 4f)
                    {
                        Vector3 lp = et.InverseTransformPoint(r.GetPoint(d));
                        if (Mathf.Abs(lp.x) < EkW / 2f && Mathf.Abs(lp.y) < EkH / 2f) EkranTikla(lp);
                    }
                }
            }
        }

        private bool EkranTikla(Vector3 lp)
        {
            if (Time.time - eTikZaman < 0.25f) return false;
            Vector2 p = new Vector2(lp.x + EkW / 2f, EkH / 2f - lp.y);
            for (int i = eTiklar.Count - 1; i >= 0; i--)
            {
                if (!eTiklar[i].Key.Contains(p)) continue;
                eTikZaman = Time.time;
                try { eTiklar[i].Value(); } catch (Exception e) { Logger.LogWarning("Wall screen click error: " + e.Message); }
                ekranImza = "";       // hemen yeniden ciz
                ekranImzaZaman = 0f;
                return true;
            }
            return false;
        }

        private string OdaAdi()
        {
            try
            {
                if (tFoton == null) tFoton = Type.GetType("Photon.Pun.PhotonNetwork, PhotonUnityNetworking");
                if (tFoton == null) return "";
                object oda = UyeAl(null, tFoton, "CurrentRoom");
                if (oda == null) return "";
                object ad = UyeAl(oda, oda.GetType(), "Name");
                return ad as string ?? "";
            }
            catch (Exception) { return ""; }
        }

        private Transform OyuncuKoku()
        {
            try
            {
                if (tTagger == null) return null;
                Component c = UyeAl(null, tTagger, "Instance") as Component;
                return c != null ? c.transform.root : null;
            }
            catch (Exception) { return null; }
        }

        // sapin ucunun onundeki zemini / duvari bul (oyuncunun kendi colliderlari ve tavan haric)
        private bool ZeminBul(Vector3 bas, Vector3 yon, float uzunluk, out RaycastHit sonuc)
        {
            sonuc = new RaycastHit();
            RaycastHit[] hits = Physics.RaycastAll(bas, yon, uzunluk, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) return false;
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            Transform kok = OyuncuKoku();
            foreach (RaycastHit h in hits)
            {
                if (h.collider == null) continue;
                Transform ht = h.collider.transform;
                if (kok != null && ht.IsChildOf(kok)) continue;
                if (yerelKopya != null && ht.IsChildOf(yerelKopya.transform)) continue;
                if (ht.name.StartsWith("GorillaFlag_")) continue;   // bayraklarin tirmanma collider'lari
                if (h.normal.y < -0.3f) continue;   // tavan / cati olmaz; zemin ve duvar olur
                sonuc = h;
                return true;
            }
            return false;
        }

        private void Sapla(RaycastHit h, float hiz, Vector3 asagi)
        {
            // sap vurdugun aciyla kalir; GF Sandbox > Upright aciksa dimdik
            Vector3 yukari = dikAcik ? Vector3.up : -asagi;
            float derinlik = Mathf.Clamp(0.03f + (hiz - SaplamaHizi) * 0.03f, 0.03f, 0.15f);   // sert vurus = daha derin

            // kopyayi oyuncudan ayir: dunyada sabit kalsin
            yerelKopya.transform.SetParent(null, true);
            yerelRgo.rotation = Quaternion.FromToRotation(-asagi, yukari) * yerelRgo.rotation;
            Vector3 uc = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapAltY, yerelSapZ));
            Vector3 hedef = h.point - yukari * derinlik;
            yerelRgo.position += hedef - uc;

            sapli = true;
            yerde = false;
            yerelTirmanKatman = h.collider != null ? h.collider.gameObject.layer : 0;
            sapliOda = OdaAdi();
            if (yerelKemikA != null && yerelKemikF != null)
            {
                Vector3 kd = yerelKemikF.position - yerelKemikA.position; kd.y = 0f;
                ruzgarYon = kd.sqrMagnitude > 1e-6f ? kd.normalized : Vector3.forward;
            }
            ruzgarTohum = UnityEngine.Random.Range(0f, 100f);
            tutuluyor = false;
            tutanEl = null;
            donusSuruyor = false;
            // arabaya saplandiysa: arabayla birlikte gitsin, tirmanma collider'i arabayi kilitlemesin
            SapliHedefAyarla(h.collider);
            Logger.LogInfo("Local flag planted at " + h.point + " (speed " + hiz.ToString("0.0") + " m/s, depth " + derinlik.ToString("0.00") + ")");
        }

        // saplanmis bayragi tekrar oyuncuya bagla
        private void SapliCoz()
        {
            sapliArac = false;
            sapliHedef = null;
            yerde = false;
            tasiyan = -1;
            if (!sapli) return;
            sapli = false;
            if (yerelKopya != null && yerelSlot != null)
            {
                yerelKopya.transform.SetParent(yerelSlot, false);
                yerelKopya.transform.localPosition = Vector3.zero;
                yerelKopya.transform.localRotation = Quaternion.identity;
                yerelKopya.transform.localScale = Vector3.one;
            }
        }
        private Vector3 oncekiSapPos, hizYumusak;
        private bool oncekiVar;

        private void PozYukle()
        {
            // herkeste ayni sabit sirt durusu (eski cfg degerleri artik kullanilmaz)
            kayitPos = new Vector3(0.17432f, 0.45419f, -0.03794f);
            kayitRot = new Quaternion(0.71382f, -0.36852f, 0.58991f, -0.08158f);
            kayitOlcek = new Vector3(0.9f, 0.9f, 0.9f);
            pozVar = true;
            if (pozCfg != null) pozCfg.Value = "";
        }

        private void PozYukleEski()
        {
            pozVar = false;
            try
            {
                string[] p = (pozCfg != null ? pozCfg.Value : "").Split(';');
                if (p.Length != 7 && p.Length != 10) return;
                float[] f = new float[p.Length];
                for (int i = 0; i < p.Length; i++) f[i] = float.Parse(p[i], CultureInfo.InvariantCulture);
                kayitPos = new Vector3(f[0], f[1], f[2]);
                kayitRot = new Quaternion(f[3], f[4], f[5], f[6]);
                kayitOlcek = p.Length == 10 ? new Vector3(f[7], f[8], f[9]) : Vector3.one;
                pozVar = true;
            }
            catch (Exception) { pozVar = false; }
            if (!pozVar)
            {
                // Pride Flag'in oyundaki gercek sirt durusu (sap kemigi * bindpose, StowSlot'a gore). Bayragi olmayanlarda da dogru cikar.
                kayitPos = new Vector3(0.17432f, 0.45419f, -0.03794f);
                kayitRot = new Quaternion(0.71382f, -0.36852f, 0.58991f, -0.08158f);
                kayitOlcek = new Vector3(0.9f, 0.9f, 0.9f);
                pozVar = true;
            }
        }

        private void PozKaydet(Vector3 p, Quaternion r)
        {
            return;   // sabit sirt durusu
            if (pozVar && (p - kayitPos).sqrMagnitude < 1e-6f && Quaternion.Angle(r, kayitRot) < 0.5f) return;
            kayitPos = p; kayitRot = r; pozVar = true;
        }

        private static Transform SlotBul(Transform t)
        {
            while (t != null) { if (t.name.IndexOf("StowSlot", StringComparison.OrdinalIgnoreCase) >= 0) return t; t = t.parent; }
            return null;
        }

        // kendi gorillamin kok objesi ("Local VRRig"); aramalar tum sahne yerine sadece bunun icinde yapilir
        private Transform YerelRigKok()
        {
            Component rig = YerelRig();
            if (rig == null) return null;
            Transform t = rig.transform;
            for (Transform p = t; p != null; p = p.parent) if (p.name == "Local VRRig") return p;
            return t.root;
        }

        // gorillanin tum kozmetiklerini taramak pahali: aday listesi 30 sn'de bir yenilenir
        private List<SkinnedMeshRenderer> kaynakAday;
        private float kaynakAdayZaman = -100f;
        private SkinnedMeshRenderer YerelKaynakBul()
        {
            if (kaynakAday == null || Time.time - kaynakAdayZaman > 30f)
            {
                kaynakAdayZaman = Time.time;
                kaynakAday = YerelKaynakAdaylar();
            }
            foreach (SkinnedMeshRenderer sk in kaynakAday)
            {
                if (sk == null) continue;
                if (yerelKopya != null && sk.transform.IsChildOf(yerelKopya.transform)) continue;
                return sk;
            }
            return null;
        }

        private List<SkinnedMeshRenderer> YerelKaynakAdaylar()
        {
            List<SkinnedMeshRenderer> sonuc = new List<SkinnedMeshRenderer>();
            string hedef = Temizle(HedefAdi);
            Transform kok = YerelRigKok();
            IEnumerable<SkinnedMeshRenderer> liste = kok != null ? (IEnumerable<SkinnedMeshRenderer>)kok.GetComponentsInChildren<SkinnedMeshRenderer>(true) : Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>();
            foreach (SkinnedMeshRenderer sk in liste)
            {
                if (sk == null || !sk.gameObject.scene.IsValid()) continue;
                if (yerelKopya != null && sk.transform.IsChildOf(yerelKopya.transform)) continue;
                if (!Temizle(sk.gameObject.name).Contains(hedef) && !Temizle(MeshAdi(sk)).Contains(hedef)) continue;
                if (!Yol(sk.transform).Contains("Local VRRig")) continue;
                sonuc.Add(sk);
            }
            return sonuc;
        }

        // Gorilla Tag "FlagPrideStandard_FBX" modelinin kopyasi (Dump mesh ile alindi). Kozmetik yuklu olmasa da kullanilir.
        private static readonly float[] GomuluV = { -0.00688f,0.23937f,-0.01189f,-0.00688f,-0.1622f,-0.01189f,-0.01373f,-0.1622f,2e-05f,-0.01373f,0.23937f,2e-05f,-0.00688f,0.64094f,-0.01189f,-0.00685f,-0.1622f,0.0119f,-0.01373f,0.64094f,2e-05f,-0.00685f,0.23937f,0.0119f,0.00688f,-0.1622f,0.01189f,-0.00685f,0.64094f,0.0119f,0.00688f,0.23937f,0.01189f,0.01373f,-0.1622f,-2e-05f,0.00688f,0.64094f,0.01189f,0.01373f,0.23937f,-2e-05f,0.00685f,-0.1622f,-0.0119f,0.01373f,0.64094f,-2e-05f,0.00685f,0.23937f,-0.0119f,-0.00688f,-0.1622f,-0.01189f,0.00685f,0.64094f,-0.0119f,-0.00688f,0.23937f,-0.01189f,-0.00688f,0.64094f,-0.01189f,-0.00688f,-0.1622f,-0.01189f,0.00685f,-0.1622f,-0.0119f,0.01373f,-0.1622f,-2e-05f,-0.01373f,-0.1622f,2e-05f,-0.00685f,-0.1622f,0.0119f,0.00688f,-0.1622f,0.01189f,-0.01373f,0.64094f,2e-05f,-0.00685f,0.64094f,0.0119f,0.00688f,0.64094f,0.01189f,0.01373f,0.64094f,-2e-05f,0.00685f,0.64094f,-0.0119f,-0.00688f,0.64094f,-0.01189f,-0.38271f,0.28545f,-0.0047f,-0.4578f,0.28545f,-0.0047f,-0.4578f,0.62591f,-0.0047f,-0.38271f,0.62591f,-0.0047f,-0.30762f,0.28545f,-0.0047f,-0.30762f,0.62591f,-0.0047f,-0.23253f,0.28545f,-0.0047f,-0.23253f,0.62591f,-0.0047f,-0.15744f,0.28545f,-0.0047f,-0.15744f,0.62591f,-0.0047f,-0.08235f,0.28545f,-0.0047f,-0.08235f,0.62591f,-0.0047f,-0.00726f,0.28545f,-0.0047f,-0.00726f,0.62591f,-0.0047f,-0.4578f,0.28545f,0.0f,-0.4578f,0.28545f,0.0047f,-0.4578f,0.62591f,0.0047f,-0.4578f,0.62591f,0.0f,-0.08235f,0.28545f,0.0047f,-0.00726f,0.28545f,0.0047f,-0.00726f,0.62591f,0.0047f,-0.08235f,0.62591f,0.0047f,-0.15744f,0.28545f,0.0047f,-0.15744f,0.62591f,0.0047f,-0.23253f,0.28545f,0.0047f,-0.23253f,0.62591f,0.0047f,-0.30762f,0.28545f,0.0047f,-0.30762f,0.62591f,0.0047f,-0.38271f,0.28545f,0.0047f,-0.38271f,0.62591f,0.0047f,-0.4578f,0.28545f,0.0047f,-0.4578f,0.62591f,0.0047f,-0.38271f,0.28545f,0.0f,-0.4578f,0.28545f,0.0047f,-0.4578f,0.28545f,0.0f,-0.38271f,0.28545f,0.0047f,-0.30762f,0.28545f,0.0f,-0.30762f,0.28545f,0.0047f,-0.23253f,0.28545f,0.0f,-0.23253f,0.28545f,0.0047f,-0.15744f,0.28545f,0.0f,-0.15744f,0.28545f,0.0047f,-0.08235f,0.28545f,0.0f,-0.08235f,0.28545f,0.0047f,-0.00726f,0.28545f,0.0f,-0.00726f,0.28545f,0.0047f,-0.08235f,0.62591f,0.0f,-0.00726f,0.62591f,0.0047f,-0.00726f,0.62591f,0.0f,-0.08235f,0.62591f,0.0047f,-0.15744f,0.62591f,0.0f,-0.15744f,0.62591f,0.0047f,-0.23253f,0.62591f,0.0f,-0.23253f,0.62591f,0.0047f,-0.30762f,0.62591f,0.0f,-0.30762f,0.62591f,0.0047f,-0.38271f,0.62591f,0.0f,-0.38271f,0.62591f,0.0047f,-0.4578f,0.62591f,0.0f,-0.4578f,0.62591f,0.0047f,-0.00726f,0.28545f,-0.0047f,-0.00726f,0.28545f,0.0f,-0.08235f,0.28545f,0.0f,-0.08235f,0.28545f,-0.0047f,-0.15744f,0.28545f,0.0f,-0.15744f,0.28545f,-0.0047f,-0.23253f,0.28545f,0.0f,-0.23253f,0.28545f,-0.0047f,-0.30762f,0.28545f,0.0f,-0.30762f,0.28545f,-0.0047f,-0.38271f,0.28545f,0.0f,-0.38271f,0.28545f,-0.0047f,-0.4578f,0.28545f,0.0f,-0.4578f,0.28545f,-0.0047f,-0.4578f,0.62591f,-0.0047f,-0.4578f,0.62591f,0.0f,-0.38271f,0.62591f,0.0f,-0.38271f,0.62591f,-0.0047f,-0.30762f,0.62591f,0.0f,-0.30762f,0.62591f,-0.0047f,-0.23253f,0.62591f,0.0f,-0.23253f,0.62591f,-0.0047f,-0.15744f,0.62591f,0.0f,-0.15744f,0.62591f,-0.0047f,-0.08235f,0.62591f,0.0f,-0.08235f,0.62591f,-0.0047f,-0.00726f,0.62591f,0.0f,-0.00726f,0.62591f,-0.0047f,-0.4578f,0.28545f,-0.0047f,-0.4578f,0.28545f,0.0f,-0.4578f,0.62591f,0.0f,-0.4578f,0.62591f,-0.0047f };
        private static readonly float[] GomuluUV = { 0.05837f,0.49918f,0.05837f,0.06116f,0.07335f,0.06116f,0.07335f,0.49918f,0.05837f,0.9372f,0.08833f,0.06116f,0.07335f,0.9372f,0.08833f,0.49918f,0.1033f,0.06116f,0.08833f,0.9372f,0.1033f,0.49918f,0.11828f,0.06116f,0.1033f,0.9372f,0.11828f,0.49918f,0.13326f,0.06116f,0.11828f,0.9372f,0.13326f,0.49918f,0.14824f,0.06116f,0.13326f,0.9372f,0.14824f,0.49918f,0.14824f,0.9372f,0.20191f,0.72257f,0.21132f,0.70627f,0.23015f,0.70627f,0.21132f,0.73888f,0.23015f,0.73888f,0.23957f,0.72257f,0.23041f,0.81121f,0.21158f,0.81121f,0.20216f,0.7949f,0.21158f,0.77859f,0.23041f,0.77859f,0.23982f,0.7949f,0.49318f,0.53705f,0.40606f,0.53705f,0.40606f,0.93203f,0.49318f,0.93203f,0.58029f,0.53705f,0.58029f,0.93203f,0.6674f,0.53705f,0.6674f,0.93203f,0.75452f,0.53705f,0.75452f,0.93203f,0.84163f,0.53705f,0.84163f,0.93203f,0.92875f,0.53705f,0.92875f,0.93203f,0.93466f,0.06984f,0.92859f,0.06984f,0.92859f,0.46483f,0.93466f,0.46483f,0.49302f,0.06984f,0.40591f,0.06984f,0.40591f,0.46483f,0.49302f,0.46483f,0.58013f,0.06984f,0.58013f,0.46483f,0.66725f,0.06984f,0.66725f,0.46483f,0.75436f,0.06984f,0.75436f,0.46483f,0.84148f,0.06984f,0.84148f,0.46483f,0.92859f,0.06984f,0.92859f,0.46483f,0.84148f,0.06378f,0.92859f,0.06984f,0.92859f,0.06378f,0.84148f,0.06984f,0.75436f,0.06378f,0.75436f,0.06984f,0.66725f,0.06378f,0.66725f,0.06984f,0.58013f,0.06378f,0.58013f,0.06984f,0.49302f,0.06378f,0.49302f,0.06984f,0.40591f,0.06378f,0.40591f,0.06984f,0.49302f,0.47039f,0.40591f,0.46483f,0.40591f,0.47039f,0.49302f,0.46483f,0.58013f,0.47039f,0.58013f,0.46483f,0.66725f,0.47039f,0.66725f,0.46483f,0.75436f,0.47039f,0.75436f,0.46483f,0.84148f,0.47039f,0.84148f,0.46483f,0.92859f,0.47039f,0.92859f,0.46483f,0.92875f,0.53705f,0.92875f,0.53104f,0.84164f,0.53102f,0.84163f,0.53705f,0.75452f,0.53101f,0.75452f,0.53705f,0.66741f,0.53099f,0.6674f,0.53705f,0.58029f,0.53097f,0.58029f,0.53705f,0.49318f,0.53095f,0.49318f,0.53705f,0.40606f,0.53094f,0.40606f,0.53705f,0.40606f,0.93203f,0.40606f,0.93754f,0.49317f,0.93756f,0.49318f,0.93203f,0.58029f,0.93758f,0.58029f,0.93203f,0.6674f,0.93759f,0.6674f,0.93203f,0.75452f,0.93761f,0.75452f,0.93203f,0.84163f,0.93763f,0.84163f,0.93203f,0.92875f,0.93765f,0.92875f,0.93203f,0.40606f,0.53705f,0.39984f,0.53705f,0.39984f,0.93203f,0.40606f,0.93203f };
        private static readonly int[] GomuluT = { 0,1,2,0,2,3,4,0,3,3,2,5,4,3,6,3,5,7,6,3,7,7,5,8,6,7,9,7,8,10,9,7,10,10,8,11,9,10,12,10,11,13,12,10,13,13,11,14,12,13,15,13,14,16,15,13,16,16,14,17,15,16,18,16,17,19,18,16,19,18,19,20,21,22,23,21,23,24,23,25,24,23,26,25,27,28,29,27,29,30,31,27,30,31,32,27,33,34,35,33,35,36,37,33,36,37,36,38,39,37,38,39,38,40,41,39,40,41,40,42,43,41,42,43,42,44,45,43,44,45,44,46,47,48,49,47,49,50,51,52,53,51,53,54,55,51,54,55,54,56,57,55,56,57,56,58,59,57,58,59,58,60,61,59,60,61,60,62,63,61,62,63,62,64,65,66,67,65,68,66,69,68,65,69,70,68,71,70,69,71,72,70,73,72,71,73,74,72,75,74,73,75,76,74,77,76,75,77,78,76,79,80,81,79,82,80,83,82,79,83,84,82,85,84,83,85,86,84,87,86,85,87,88,86,89,88,87,89,90,88,91,90,89,91,92,90,93,94,95,93,95,96,96,95,97,96,97,98,98,97,99,98,99,100,100,99,101,100,101,102,102,101,103,102,103,104,104,103,105,104,105,106,107,108,109,107,109,110,110,109,111,110,111,112,112,111,113,112,113,114,114,113,115,114,115,116,116,115,117,116,117,118,118,117,119,118,119,120,121,122,123,121,123,124 };

        // Orijinal modelin kemik agirliklari (kose basina 4 kemik), gercek bayraktan olculdu
        private static readonly int[] GomuluBI = { 1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,5,4,7,6,5,4,7,4,5,6,3,5,4,6,3,4,3,5,6,3,4,2,5,3,2,4,5,3,2,4,5,2,0,0,0,2,0,0,0,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,3,2,4,5,2,0,0,0,2,0,0,0,3,2,4,5,4,3,5,6,3,4,2,5,4,5,6,3,5,4,6,3,6,5,4,7,6,5,4,7,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,5,4,7,6,5,4,7,4,5,6,3,4,5,6,3,4,3,5,6,4,3,5,6,3,2,4,5,3,2,4,5,2,0,0,0,2,0,0,0,3,2,4,5,2,0,0,0,2,0,0,0,3,2,4,5,3,4,2,5,3,4,2,5,5,4,6,3,5,4,6,3,6,5,4,7,6,5,4,7,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,2,0,0,0,2,0,0,0,3,2,4,5,3,2,4,5,4,3,5,6,4,3,5,6,4,5,6,3,4,5,6,3,6,5,4,7,6,5,4,7,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4,6,5,4,7,6,5,4,7,5,4,6,3,5,4,6,3,3,4,2,5,3,4,2,5,3,2,4,5,3,2,4,5,2,0,0,0,2,0,0,0,6,7,5,4,6,7,5,4,6,7,5,4,6,7,5,4 };
        private static readonly float[] GomuluBW = { 1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,1f,0f,0f,0f,0.48297f,0.30745f,0.15301f,0.05656f,0.54699f,0.27076f,0.13444f,0.04781f,0.54978f,0.27762f,0.13125f,0.04134f,0.48585f,0.31387f,0.14805f,0.05222f,0.39899f,0.35355f,0.13103f,0.11643f,0.40188f,0.35845f,0.12609f,0.11358f,0.36967f,0.36799f,0.155f,0.10734f,0.36313f,0.36249f,0.1452f,0.12918f,0.43796f,0.33282f,0.16138f,0.06784f,0.37041f,0.36533f,0.13548f,0.12877f,0.38258f,0.37936f,0.17403f,0.06403f,0.40362f,0.40022f,0.14694f,0.04921f,1f,0f,0f,0f,1f,0f,0f,0f,0.54382f,0.27246f,0.13529f,0.04844f,0.54065f,0.27415f,0.13613f,0.04907f,0.55661f,0.27386f,0.12947f,0.04006f,0.55319f,0.27574f,0.13036f,0.0407f,0.38258f,0.37936f,0.17403f,0.06403f,1f,0f,0f,0f,1f,0f,0f,0f,0.40362f,0.40022f,0.14694f,0.04921f,0.43796f,0.33282f,0.16138f,0.06784f,0.37041f,0.36533f,0.13548f,0.12877f,0.36967f,0.36799f,0.15499f,0.10734f,0.36313f,0.36248f,0.14521f,0.12918f,0.39892f,0.35357f,0.13104f,0.11647f,0.40195f,0.35843f,0.12608f,0.11354f,0.48237f,0.30781f,0.15319f,0.05663f,0.48649f,0.31348f,0.14786f,0.05216f,0.54065f,0.27415f,0.13613f,0.04907f,0.55661f,0.27386f,0.12947f,0.04006f,0.48267f,0.30763f,0.1531f,0.05659f,0.54065f,0.27415f,0.13613f,0.04907f,0.54382f,0.27246f,0.13529f,0.04844f,0.48237f,0.30781f,0.15319f,0.05663f,0.39896f,0.35356f,0.13104f,0.11645f,0.39892f,0.35357f,0.13104f,0.11647f,0.36967f,0.36799f,0.15499f,0.10734f,0.36967f,0.36799f,0.15499f,0.10734f,0.43796f,0.33282f,0.16138f,0.06784f,0.43796f,0.33282f,0.16138f,0.06784f,0.38258f,0.37936f,0.17403f,0.06403f,0.38258f,0.37936f,0.17403f,0.06403f,1f,0f,0f,0f,1f,0f,0f,0f,0.40362f,0.40022f,0.14694f,0.04921f,1f,0f,0f,0f,1f,0f,0f,0f,0.40362f,0.40022f,0.14694f,0.04921f,0.37041f,0.36533f,0.13548f,0.12877f,0.37041f,0.36533f,0.13548f,0.12877f,0.36313f,0.36248f,0.1452f,0.12918f,0.36313f,0.36248f,0.14521f,0.12918f,0.40192f,0.35844f,0.12608f,0.11356f,0.40195f,0.35843f,0.12608f,0.11354f,0.48617f,0.31368f,0.14796f,0.05219f,0.48649f,0.31348f,0.14786f,0.05216f,0.55319f,0.27574f,0.13036f,0.0407f,0.55661f,0.27386f,0.12947f,0.04006f,1f,0f,0f,0f,1f,0f,0f,0f,0.38258f,0.37936f,0.17403f,0.06403f,0.38258f,0.37936f,0.17403f,0.06403f,0.43796f,0.33282f,0.16138f,0.06784f,0.43796f,0.33282f,0.16138f,0.06784f,0.36967f,0.36799f,0.15499f,0.10734f,0.36967f,0.36799f,0.155f,0.10734f,0.39896f,0.35356f,0.13104f,0.11645f,0.39899f,0.35355f,0.13103f,0.11643f,0.48267f,0.30763f,0.1531f,0.05659f,0.48297f,0.30745f,0.15301f,0.05656f,0.54382f,0.27246f,0.13529f,0.04844f,0.54699f,0.27076f,0.13444f,0.04781f,0.54978f,0.27762f,0.13125f,0.04134f,0.55319f,0.27574f,0.13036f,0.0407f,0.48617f,0.31368f,0.14796f,0.05219f,0.48585f,0.31387f,0.14805f,0.05222f,0.40192f,0.35844f,0.12608f,0.11356f,0.40188f,0.35845f,0.12609f,0.11358f,0.36313f,0.36248f,0.1452f,0.12918f,0.36313f,0.36249f,0.1452f,0.12918f,0.37041f,0.36533f,0.13548f,0.12877f,0.37041f,0.36533f,0.13548f,0.12877f,0.40362f,0.40022f,0.14694f,0.04921f,0.40362f,0.40022f,0.14694f,0.04921f,1f,0f,0f,0f,1f,0f,0f,0f,0.54699f,0.27076f,0.13444f,0.04781f,0.54382f,0.27246f,0.13529f,0.04844f,0.55319f,0.27574f,0.13036f,0.0407f,0.54978f,0.27762f,0.13125f,0.04134f };

        // Oyundaki modelin birebir aynisi (fizikli kopya icin)
        // ---------- Bayrak detayi: kumasi 6 yerine daha cok parcaya bol (kemikler ve fizik ayni) ----------
        private static readonly int[] DetayDegerleri = { 6, 12, 24, 48 };
        private int detay = 6;
        private BepInEx.Configuration.ConfigEntry<int> detayCfg;
        private readonly Dictionary<int, Mesh> detayMeshler = new Dictionary<int, Mesh>();
        private readonly Dictionary<SkinnedMeshRenderer, Mesh> orijinalMeshler = new Dictionary<SkinnedMeshRenderer, Mesh>();
        private float detayZaman;

        private static Mesh DetayMesh(int NX)
        {
            return SekilMesh(NX, 0);
        }

        // ---------- Bayrak sekli: kare bayraklar ve Nepal kendi seklinde ----------
        // 0 = normal dikdortgen, 1 = kare (Isvicre, Vatikan), 2 = Nepal
        private int sekil = 0;
        private static readonly float[] SekilOran = { 0.45054f / 0.34046f, 1f, 0.784f };   // en / boy
        // her blok: {v0, v1, u0, u1}  -> v0..v1 satirlarinda kumas 0..umax(v) arasinda (umax: u0'dan u1'e dogrusal)
        private static readonly float[][][] SekilBlok =
        {
            new float[][] { new float[] { 0f, 1f, 1f, 1f } },
            new float[][] { new float[] { 0f, 1f, 1f, 1f } },
            new float[][] { new float[] { 0f, 0.517f, 1f, 0.349f }, new float[] { 0.517f, 1f, 0.988f, 0f } },
        };
        // dis cevre (u: saptan uca, v: alttan uste)
        private static readonly Vector2[][] SekilCevre =
        {
            new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
            new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
            new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.349f, 0.517f), new Vector2(0.988f, 0.517f), new Vector2(0, 1) },
        };

        private static int SekilAdindan(string ad)
        {
            string l = (ad ?? "").ToLowerInvariant();
            if (l == "switzerland" || l == "vatican") return 1;
            if (l == "nepal") return 2;
            return 0;
        }

        private int SekilBul(int i)
        {
            if (i < 0 || i >= isimler.Count || i >= kategoriler.Count || kategoriler[i] != 0) return 0;
            return SekilAdindan(isimler[i]);
        }

        private void SekilGuncelle()
        {
            int yeni = SekilBul(secili);
            if (yeni == sekil) return;
            sekil = yeni;
            YerelKaldir();          // Private kopya yeni sekille yeniden kurulsun
            yerelZaman = 0f;
            try { GercekMeshUygula(); } catch (Exception e) { Logger.LogWarning("Flag shape error: " + e.Message); }
        }

        private static Mesh SekilMesh(int NX, int sk)
        {
            const int SAP = 33;
            const float X1 = -0.00726f, Y0 = 0.28545f, Y1 = 0.62591f, Z = 0.0047f, W43 = 0.45054f;
            float Hc = Y1 - Y0;
            float Wc = Hc * SekilOran[sk];
            List<Vector3> v = new List<Vector3>();
            List<Vector2> uv = new List<Vector2>();
            List<BoneWeight> bw = new List<BoneWeight>();
            List<int> tri = new List<int>();
            for (int i = 0; i < SAP; i++)
            {
                v.Add(new Vector3(GomuluV[i * 3], GomuluV[i * 3 + 1], GomuluV[i * 3 + 2]));
                uv.Add(new Vector2(GomuluUV[i * 2], GomuluUV[i * 2 + 1]));
                BoneWeight b = new BoneWeight(); b.boneIndex0 = 1; b.weight0 = 1f; bw.Add(b);
            }
            for (int i = 0; i + 2 < GomuluT.Length; i += 3)
                if (GomuluT[i] < SAP && GomuluT[i + 1] < SAP && GomuluT[i + 2] < SAP)
                { tri.Add(GomuluT[i]); tri.Add(GomuluT[i + 1]); tri.Add(GomuluT[i + 2]); }

            // (u,v) -> fiziksel konum; doku her zaman bayrak bolgesinin tamamini kaplar
            System.Func<float, float> fx = u => X1 - u * Wc;
            System.Func<float, float> fy = vv => Y0 + vv * Hc;
            System.Func<float, float, bool, Vector2> dokuUV = (u, vv, on) =>
            {
                float xs = X1 - u * W43, ys = Y0 + vv * Hc;
                return on ? new Vector2(1.16013f * xs + 0.93717f, 1.16014f * ys + 0.20589f)
                          : new Vector2(-1.16013f * xs + 0.39748f, 1.16017f * ys - 0.26133f);
            };

            int NYtop = Mathf.Max(4, Mathf.RoundToInt(NX * 0.75f));
            foreach (float[] blok in SekilBlok[sk])
            {
                float v0 = blok[0], v1 = blok[1], u0 = blok[2], u1 = blok[3];
                int NY = Mathf.Max(2, Mathf.RoundToInt(NYtop * (v1 - v0)));
                for (int yuz = 0; yuz < 2; yuz++)
                {
                    bool on = yuz == 0;
                    int bas = v.Count;
                    for (int j = 0; j <= NY; j++)
                    {
                        float vv = Mathf.Lerp(v0, v1, j / (float)NY);
                        float umax = Mathf.Lerp(u0, u1, j / (float)NY);
                        for (int i = 0; i <= NX; i++)
                        {
                            float u = umax * i / NX;
                            float x = fx(u), y = fy(vv);
                            v.Add(new Vector3(x, y, on ? -Z : Z));
                            uv.Add(dokuUV(u, vv, on));
                            bw.Add(KumasAgirlik(x));
                        }
                    }
                    for (int j = 0; j < NY; j++)
                        for (int i = 0; i < NX; i++)
                        {
                            int a = bas + j * (NX + 1) + i, b = a + 1, d = a + NX + 1, c = d + 1;
                            if (on) { tri.Add(a); tri.Add(b); tri.Add(c); tri.Add(a); tri.Add(c); tri.Add(d); }
                            else { tri.Add(a); tri.Add(c); tri.Add(b); tri.Add(a); tri.Add(d); tri.Add(c); }
                        }
                }
            }

            // kenar kalinligi: dis cevre boyunca ince serit
            Vector2[] cevre = SekilCevre[sk];
            Vector2 merkez = Vector2.zero;
            foreach (Vector2 p in cevre) merkez += p;
            merkez /= cevre.Length;
            for (int e = 0; e < cevre.Length; e++)
            {
                Vector2 p0 = cevre[e], p1 = cevre[(e + 1) % cevre.Length];
                int K = Mathf.Max(2, Mathf.RoundToInt(Vector2.Distance(p0, p1) * NX));
                int bas = v.Count;
                for (int k = 0; k <= K; k++)
                {
                    Vector2 p = Vector2.Lerp(p0, p1, k / (float)K);
                    Vector2 ic = Vector2.Lerp(p, merkez, 0.02f);   // doku: kenarin biraz ici (kenar rengi)
                    float x = fx(p.x), y = fy(p.y);
                    v.Add(new Vector3(x, y, -Z)); uv.Add(dokuUV(ic.x, ic.y, true)); bw.Add(KumasAgirlik(x));
                    v.Add(new Vector3(x, y, Z)); uv.Add(dokuUV(ic.x, ic.y, true)); bw.Add(KumasAgirlik(x));
                }
                // disa bakan yon (fiziksel duzlemde)
                Vector2 q0 = new Vector2(fx(p0.x), fy(p0.y)), q1 = new Vector2(fx(p1.x), fy(p1.y));
                Vector2 qm = new Vector2(fx(merkez.x), fy(merkez.y));
                Vector2 dis = (q0 + q1) * 0.5f - qm;
                for (int k = 0; k < K; k++)
                {
                    int f0 = bas + k * 2, b0 = f0 + 1, f1 = f0 + 2, b1 = f0 + 3;
                    Vector3 n = Vector3.Cross(v[f1] - v[f0], v[b0] - v[f0]);
                    bool ters = n.x * dis.x + n.y * dis.y < 0f;
                    if (!ters) { tri.Add(f0); tri.Add(f1); tri.Add(b0); tri.Add(f1); tri.Add(b1); tri.Add(b0); }
                    else { tri.Add(f0); tri.Add(b0); tri.Add(f1); tri.Add(f1); tri.Add(b0); tri.Add(b1); }
                }
            }

            Mesh m = new Mesh();
            m.name = "FlagPrideStandard_FBX_shape" + sk + "_" + NX;
            m.SetVertices(v);
            m.SetUVs(0, uv);
            m.boneWeights = bw.ToArray();
            m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private Mesh FizikMesh(float ek = 0f, float ol = 1f, float kal = 1f)
        {
            Mesh m = (sekil == 0 && detay <= 6) ? OrijinalMesh() : SekilMesh(sekil != 0 ? Mathf.Max(detay, 12) : detay, sekil);
            BoyUygula(m, ek, ol, kal);
            return m;
        }

        // Gercek (oyundaki) Pride bayraginda da detayli modeli kullan: sadece bu bilgisayarda gorunur
        private void GercekMeshUygula()
        {
            bool geriAl = (detay <= 6 && sekil == 0) || kozmetik == 3;
            List<SkinnedMeshRenderer> olu = new List<SkinnedMeshRenderer>();
            foreach (var kv in orijinalMeshler)
            {
                if (kv.Key == null) { olu.Add(kv.Key); continue; }
                if (geriAl) kv.Key.sharedMesh = kv.Value;
            }
            foreach (var k in olu) orijinalMeshler.Remove(k);
            if (geriAl) { orijinalMeshler.Clear(); return; }

            foreach (Renderer r in Hedefler())
            {
                SkinnedMeshRenderer sk = r as SkinnedMeshRenderer;
                if (sk == null || sk.sharedMesh == null) continue;
                if (yerelKopya != null && sk.transform.IsChildOf(yerelKopya.transform)) continue;
                if (sk.bones == null || sk.bones.Length != 8) continue;
                Mesh org;
                if (!orijinalMeshler.TryGetValue(sk, out org))
                {
                    if (sk.sharedMesh.name.IndexOf("FlagPrideStandard", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (sk.sharedMesh.vertexCount != GomuluV.Length / 3) continue;
                    org = sk.sharedMesh;
                    orijinalMeshler[sk] = org;
                }
                Mesh m;
                int nx = sekil != 0 ? Mathf.Max(detay, 12) : detay;
                int anahtar = nx * 10 + sekil;
                if (!detayMeshler.TryGetValue(anahtar, out m) || m == null)
                {
                    m = SekilMesh(nx, sekil);
                    m.bindposes = org.bindposes;
                    detayMeshler[anahtar] = m;
                }
                if (sk.sharedMesh != m) sk.sharedMesh = m;
            }
        }

        private void DetaySec(int d)
        {
            if (d == detay) return;
            detay = d;
            if (detayCfg != null) detayCfg.Value = d;
            YerelKaldir();          // Private kopya yeni modelle yeniden kurulsun
            yerelZaman = 0f;
            GercekMeshUygula();
            foreach (var kv in kayitlar) kv.Value.uygulanan = "";
        }

        private static Mesh OrijinalMesh()
        {
            int n = GomuluV.Length / 3;
            Vector3[] v = new Vector3[n];
            Vector2[] uv = new Vector2[n];
            BoneWeight[] bw = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                v[i] = new Vector3(GomuluV[i * 3], GomuluV[i * 3 + 1], GomuluV[i * 3 + 2]);
                uv[i] = new Vector2(GomuluUV[i * 2], GomuluUV[i * 2 + 1]);
                bw[i].boneIndex0 = GomuluBI[i * 4]; bw[i].weight0 = GomuluBW[i * 4];
                bw[i].boneIndex1 = GomuluBI[i * 4 + 1]; bw[i].weight1 = GomuluBW[i * 4 + 1];
                bw[i].boneIndex2 = GomuluBI[i * 4 + 2]; bw[i].weight2 = GomuluBW[i * 4 + 2];
                bw[i].boneIndex3 = GomuluBI[i * 4 + 3]; bw[i].weight3 = GomuluBW[i * 4 + 3];
            }
            Mesh m = new Mesh();
            m.name = "FlagPrideStandard_FBX";
            m.vertices = v;
            m.uv = uv;
            m.triangles = GomuluT;
            m.boneWeights = bw;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static Mesh GomuluMesh()
        {
            // Sap: orijinal modelden (ilk 33 kose). Kumas: dalga yumusak olsun diye ince izgara olarak yeniden uretilir
            // (orijinalde 6 sutun var, dalgalaninca koseli gorunuyordu). UV'ler orijinal kumasla birebir ayni.
            const int SAP = 33, NX = 28, NY = 10;
            const float X0 = -0.4578f, X1 = -0.00726f, Y0 = 0.28545f, Y1 = 0.62591f, Z = 0.0047f;
            List<Vector3> v = new List<Vector3>();
            List<Vector2> uv = new List<Vector2>();
            List<int> tri = new List<int>();
            for (int i = 0; i < SAP; i++)
            {
                v.Add(new Vector3(GomuluV[i * 3], GomuluV[i * 3 + 1], GomuluV[i * 3 + 2]));
                uv.Add(new Vector2(GomuluUV[i * 2], GomuluUV[i * 2 + 1]));
            }
            for (int i = 0; i + 2 < GomuluT.Length; i += 3)
                if (GomuluT[i] < SAP && GomuluT[i + 1] < SAP && GomuluT[i + 2] < SAP)
                { tri.Add(GomuluT[i]); tri.Add(GomuluT[i + 1]); tri.Add(GomuluT[i + 2]); }
            for (int yuz = 0; yuz < 2; yuz++)
            {
                bool on = yuz == 0;   // on yuz z=-0.0047
                int bas = v.Count;
                for (int j = 0; j <= NY; j++)
                    for (int i = 0; i <= NX; i++)
                    {
                        float x = Mathf.Lerp(X0, X1, i / (float)NX), y = Mathf.Lerp(Y0, Y1, j / (float)NY);
                        v.Add(new Vector3(x, y, on ? -Z : Z));
                        uv.Add(on ? new Vector2(1.16013f * x + 0.93717f, 1.16014f * y + 0.20589f)
                                  : new Vector2(-1.16013f * x + 0.39748f, 1.16017f * y - 0.26133f));
                    }
                for (int j = 0; j < NY; j++)
                    for (int i = 0; i < NX; i++)
                    {
                        int a = bas + j * (NX + 1) + i, b = a + 1, d = a + NX + 1, c = d + 1;
                        if (on) { tri.Add(a); tri.Add(c); tri.Add(b); tri.Add(a); tri.Add(d); tri.Add(c); }
                        else { tri.Add(a); tri.Add(b); tri.Add(c); tri.Add(a); tri.Add(c); tri.Add(d); }
                    }
            }
            // kumasin kalinlik kenarlari (ust, alt, uc, sap tarafi): orijinal modeldeki gibi ince seritler
            System.Func<float, float, Vector2> onUV = (x, y) => new Vector2(1.16013f * x + 0.93717f, 1.16014f * y + 0.20589f);
            for (int kenar = 0; kenar < 4; kenar++)
            {
                int bas = v.Count;
                int adim = kenar < 2 ? NX : NY;
                for (int k = 0; k <= adim; k++)
                {
                    float x, y;
                    if (kenar == 0) { x = Mathf.Lerp(X0, X1, k / (float)NX); y = Y1; }        // ust
                    else if (kenar == 1) { x = Mathf.Lerp(X0, X1, k / (float)NX); y = Y0; }   // alt
                    else if (kenar == 2) { x = X0; y = Mathf.Lerp(Y0, Y1, k / (float)NY); }   // uc
                    else { x = X1; y = Mathf.Lerp(Y0, Y1, k / (float)NY); }                   // sap tarafi
                    // UV kenarin biraz icinden: bayragin kenar rengi gorunsun
                    Vector2 t = onUV(Mathf.Clamp(x, X0 + 0.004f, X1 - 0.004f), Mathf.Clamp(y, Y0 + 0.004f, Y1 - 0.004f));
                    v.Add(new Vector3(x, y, -Z)); uv.Add(t);
                    v.Add(new Vector3(x, y, Z)); uv.Add(t);
                }
                for (int k = 0; k < adim; k++)
                {
                    int p0 = bas + k * 2, p3 = p0 + 1, p1 = p0 + 2, p2 = p0 + 3;   // p0,p1 on (z-) ; p3,p2 arka (z+)
                    bool ters = kenar == 0 || kenar == 2;   // ust (+y) ve uc (-x) disa baksin
                    if (ters) { tri.Add(p0); tri.Add(p2); tri.Add(p1); tri.Add(p0); tri.Add(p3); tri.Add(p2); }
                    else { tri.Add(p0); tri.Add(p1); tri.Add(p2); tri.Add(p0); tri.Add(p2); tri.Add(p3); }
                }
            }

            Mesh m = new Mesh();
            m.name = "FlagPrideStandard_FBX";
            m.SetVertices(v);
            m.SetUVs(0, uv);
            m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            m.MarkDynamic();
            return m;
        }

        private Vector3 kayitOlcek = Vector3.one;
        private float ogrenZaman;
        // Sol omuz (bayrak sol elle birakilinca oyun onu sol omuza koyar). Sag StowSlot'a gore olculdu.
        private static readonly Vector3 SolPos = new Vector3(-0.17432f, 0.45419f, -0.03794f);
        private static readonly Quaternion SolRot = new Quaternion(0.83893f, 0.08425f, 0.39208f, -0.36792f);
        private bool sirtSol;
        private BepInEx.Configuration.ConfigEntry<int> tarafCfg;   // 0 sag omuz, 1 sol omuz

        // Elde tutus: gercek bayrak elde tutulurken sap matrisinin ele gore durusu (sol=0, sag=1)
        private readonly BepInEx.Configuration.ConfigEntry<string>[] tutCfg = new BepInEx.Configuration.ConfigEntry<string>[2];
        private readonly bool[] tutVar = new bool[2];
        private readonly Vector3[] tutPos = new Vector3[2];
        private readonly Quaternion[] tutRot = { Quaternion.identity, Quaternion.identity };
        private float tutOgrenZaman;

        // Elde tutus (gorillanin EL KEMIGINE gore): gercek bayrak da el kemigine bagli oldugu icin avucun icinde durur
        private readonly BepInEx.Configuration.ConfigEntry<string>[] tutKCfg = new BepInEx.Configuration.ConfigEntry<string>[2];
        private readonly bool[] tutKVar = new bool[2];
        private readonly Vector3[] tutKPos = new Vector3[2];
        private readonly Quaternion[] tutKRot = { Quaternion.identity, Quaternion.identity };

        private void TutKYukle(int h)
        {
            // herkes ayni avuc ici tutusu kullanir: gercek Pride Flag'den olculdu.
            // Eski surumlerin cfg'ye yazdigi (bazen hatali) ogrenilmis degerler artik kullanilmaz.
            tutKPos[h] = h == 0 ? new Vector3(-0.04185f, 0.06902f, 0.03879f) : new Vector3(0.04185f, 0.06904f, 0.0388f);
            tutKRot[h] = h == 0 ? new Quaternion(0.30516f, 0.70713f, 0.4824f, 0.4173f) : new Quaternion(0.49526f, 0.3999f, 0.28429f, 0.71693f);
            tutKVar[h] = true;
        }

        private void TutKKaydet(int h, Vector3 p, Quaternion r)
        {
            return;   // sabit avuc ici tutusu: ogrenip uzerine yazma yok
            if (tutKVar[h] && (p - tutKPos[h]).sqrMagnitude < 4e-6f && Quaternion.Angle(r, tutKRot[h]) < 1f) return;
            bool yeni = !tutKVar[h];
            tutKPos[h] = p; tutKRot[h] = r; tutKVar[h] = true;
            if (tutKCfg[h] != null)
                tutKCfg[h].Value = string.Join(";", new string[] { F(p.x), F(p.y), F(p.z), F(r.x), F(r.y), F(r.z), F(r.w) });
            if (yeni) Logger.LogInfo("Local flag hand-bone hold pose learned (" + (h == 0 ? "left" : "right") + "): " + p + " " + r.eulerAngles);
        }

        private void TutYukle(int h)
        {
            tutPos[h] = h == 0 ? new Vector3(0.0091f, 0.01109f, -0.03731f) : new Vector3(-0.01169f, 0.00864f, -0.03682f);
            tutRot[h] = h == 0 ? new Quaternion(0.37552f, -0.48311f, -0.42454f, 0.66735f) : new Quaternion(-0.42064f, 0.67793f, 0.39974f, -0.45131f);
            tutVar[h] = true;
            return;   // herkeste ayni sabit tutus
            tutVar[h] = false;
            try
            {
                string[] p = (tutCfg[h] != null ? tutCfg[h].Value : "").Split(';');
                if (p.Length != 7) return;
                float[] f = new float[7];
                for (int i = 0; i < 7; i++) f[i] = float.Parse(p[i], CultureInfo.InvariantCulture);
                tutPos[h] = new Vector3(f[0], f[1], f[2]);
                tutRot[h] = new Quaternion(f[3], f[4], f[5], f[6]);
                tutVar[h] = true;
            }
            catch (Exception) { tutVar[h] = false; }
            if (!tutVar[h])
            {
                // Pride Flag'in oyundaki gercek elde tutusu (olculdu)
                if (h == 0) { tutPos[0] = new Vector3(0.01514f, 0.018f, -0.02979f); tutRot[0] = new Quaternion(0.36527f, -0.47572f, -0.42165f, 0.68006f); }
                else { tutPos[1] = new Vector3(-0.01013f, 0.01075f, -0.03799f); tutRot[1] = new Quaternion(-0.41959f, 0.68493f, 0.38234f, -0.45676f); }
                tutVar[h] = true;
            }
        }

        private void TutKaydet(int h, Vector3 p, Quaternion r)
        {
            return;   // sabit tutus
            if (tutVar[h] && (p - tutPos[h]).sqrMagnitude < 4e-6f && Quaternion.Angle(r, tutRot[h]) < 1f) return;
            bool yeni = !tutVar[h];
            tutPos[h] = p; tutRot[h] = r; tutVar[h] = true;
            if (tutCfg[h] != null)
                tutCfg[h].Value = string.Join(";", new string[] { F(p.x), F(p.y), F(p.z), F(r.x), F(r.y), F(r.z), F(r.w) });
            if (yeni) Logger.LogInfo("Local flag hold pose learned (" + (h == 0 ? "left" : "right") + "): " + p + " " + r.eulerAngles);
        }

        // Gercek bayrak elde tutuluyorsa elin ona gore durusunu ogren (LateUpdate'te, oyun eli yerlestirdikten sonra)
        private void TutOgren()
        {
            if (!gizli || yerelKaynak == null || yerelSlot == null || !pozVar) return;
            if (!yerelKaynak.gameObject.activeInHierarchy) return;
            if (Time.time - tutOgrenZaman < 0.3f) return;
            tutOgrenZaman = Time.time;
            Matrix4x4 m;
            if (!SapMatrisi(yerelKaynak, out m)) return;
            Vector3 alt = m.MultiplyPoint3x4(new Vector3(0f, -0.1622f, 0f));
            Vector3 ust = m.MultiplyPoint3x4(new Vector3(0f, 0.64094f, 0f));
            // sirttaysa ogrenme
            if (Yol(yerelKaynak.transform).Contains("StowSlot")) return;
            int enIyi = -1; float enYakin = 0.25f;
            for (int h = 0; h < 2; h++)
            {
                Transform el = El(h);
                if (el == null || !Tutuyor(h)) continue;
                float d = NoktaDogru(el.position, alt, ust);
                if (d < enYakin) { enYakin = d; enIyi = h; }
            }
            if (enIyi < 0) return;
            Transform e = El(enIyi);
            Matrix4x4 lok = e.worldToLocalMatrix * m;
            TutKaydet(enIyi, lok.GetColumn(3), Quaternion.LookRotation(lok.GetColumn(2), lok.GetColumn(1)));
            // gorillanin el kemigine gore de ogren (gercek bayrak bu kemige bagli)
            Transform kemik = RigEl(YerelRig(), enIyi);
            if (kemik != null)
            {
                Matrix4x4 lk = kemik.worldToLocalMatrix * m;
                Vector3 kx = lk.GetColumn(0), ky = lk.GetColumn(1), kz = lk.GetColumn(2);
                // kemigin olcegi rgo'ya tasinmasin diye sadece yon + konum (kemik uzayinda)
                TutKKaydet(enIyi, lk.GetColumn(3), Quaternion.LookRotation(kz, ky));
            }
        }

        private bool SapMatrisi(SkinnedMeshRenderer sk, out Matrix4x4 sonuc)
        {
            sonuc = Matrix4x4.identity;
            try
            {
                Mesh m = sk.sharedMesh;
                Transform[] kemik = sk.bones;
                if (m == null) return false;
                if (kemik == null || kemik.Length == 0 || m.bindposes == null || m.bindposes.Length == 0)
                {
                    sonuc = sk.transform.localToWorldMatrix;   // kemiksiz ise normal donusum
                    return true;
                }
                BoneWeight[] w = m.boneWeights;
                Vector2[] uv = m.uv;
                int[] say = new int[kemik.Length];
                for (int i = 0; i < w.Length && i < uv.Length; i++)
                    if (uv[i].x * 256f < 81f && w[i].boneIndex0 < say.Length) say[w[i].boneIndex0]++;
                int b = 0;
                for (int i = 1; i < say.Length; i++) if (say[i] > say[b]) b = i;
                if (kemik[b] == null || b >= m.bindposes.Length) return false;
                sonuc = kemik[b].localToWorldMatrix * m.bindposes[b];
                return true;
            }
            catch (Exception) { return false; }
        }
        private float aramaZaman;
        private Material yerelMat;

        private Transform SlotAra()
        {
            Transform kok = YerelRigKok();
            if (kok != null)
            {
                Transform s0 = AltBul(kok, "body_AnchorBackRight_StowSlot");
                if (s0 != null) return s0;
            }
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                if (t.name != "body_AnchorBackRight_StowSlot") continue;
                if (!Yol(t).Contains("Local VRRig")) continue;
                return t;
            }
            return null;
        }

        // Atlas dokusu kullanan herhangi bir oyun materyali (Uber shader). Kopyalanip bayrak icin kullanilir.
        private Material KaynakMateryal()
        {
            Material yedek = null;
            Transform kok = YerelRigKok();
            IEnumerable<Renderer> liste = kok != null ? (IEnumerable<Renderer>)kok.GetComponentsInChildren<Renderer>(true) : FindObjectsOfType<Renderer>();
            foreach (Renderer r in liste)
            {
                Material m = r.sharedMaterial;
                if (m == null || m.shader == null || m.shader.name != "GorillaTag/UberShader") continue;
                if (!m.HasProperty("_BaseMap_Atlas") || !m.HasProperty("_BaseMap_AtlasSlice")) continue;
                if (!(m.GetTexture("_BaseMap_Atlas") is Texture2DArray)) continue;
                if (Yol(r.transform).Contains("Local VRRig")) return m;
                if (yedek == null) yedek = m;
            }
            if (yedek == null && kok != null)
                foreach (Renderer r in FindObjectsOfType<Renderer>())
                {
                    Material m = r.sharedMaterial;
                    if (m == null || m.shader == null || m.shader.name != "GorillaTag/UberShader") continue;
                    if (!m.HasProperty("_BaseMap_Atlas") || !m.HasProperty("_BaseMap_AtlasSlice")) continue;
                    if (!(m.GetTexture("_BaseMap_Atlas") is Texture2DArray)) continue;
                    return m;
                }
            return yedek;
        }

        private void GomuluSablonYukle()
        {
            if (sablonlar.ContainsKey(276)) return;
            try
            {
                System.Reflection.Assembly asm = typeof(Plugin).Assembly;
                using (Stream st = asm.GetManifestResourceStream("gorillaflag.atlas276.dat"))
                {
                    if (st == null) return;
                    MemoryStream ms = new MemoryStream();
                    st.CopyTo(ms);
                    Type tip = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                    var yukle = tip.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]) });
                    Texture2D tex = new Texture2D(2, 2);
                    yukle.Invoke(null, new object[] { tex, ms.ToArray() });
                    if (tex.width == 256 && tex.height == 256) { sablonlar[276] = tex.GetPixels32(); SapKarartiSil(sablonlar[276], 256); }
                    Destroy(tex);
                }
            }
            catch (Exception e) { Logger.LogWarning("Embedded template error: " + e.Message); }
        }

        private void PozKaydet3(Vector3 p, Quaternion r, Vector3 sc)
        {
            return;   // sabit sirt durusu
            if (pozVar && (p - kayitPos).sqrMagnitude < 1e-6f && Quaternion.Angle(r, kayitRot) < 0.5f && (sc - kayitOlcek).sqrMagnitude < 1e-6f) return;
            kayitOlcek = sc;
            kayitPos = p; kayitRot = r; pozVar = true;
            if (pozCfg != null)
                pozCfg.Value = string.Join(";", new string[] { F(p.x), F(p.y), F(p.z), F(r.x), F(r.y), F(r.z), F(r.w), F(sc.x), F(sc.y), F(sc.z) });
        }

        private void YerelGuncelle()
        {
            if (!gizli) { YerelKaldir(); return; }

            bool ara = Time.time - aramaZaman > 2f;
            if (ara) aramaZaman = Time.time;

            if (yerelSlot == null && ara) yerelSlot = SlotAra();
            if (yerelKaynak == null && ara) yerelKaynak = YerelKaynakBul();

            // Oyunun kendi bayrak modeli yuklenmisse sirttaki tam durusunu ogren ve kalici kaydet
            if (yerelKaynak != null && yerelSlot != null)
            {
                // Skinned mesh: gorunen sekli kemikler belirler. Sap kemiginin dunya matrisi * bindpose = mesh'in gercek yerlesimi.
                if (yerelKaynak.gameObject.activeInHierarchy && Time.time - ogrenZaman > 0.5f && Yol(yerelKaynak.transform).Contains("BackRight"))
                {
                    ogrenZaman = Time.time;
                    Matrix4x4 m;
                    if (SapMatrisi(yerelKaynak, out m))
                    {
                        Matrix4x4 lok = yerelSlot.worldToLocalMatrix * m;
                        Vector3 pos = lok.GetColumn(3);
                        Vector3 sx = lok.GetColumn(0), sy = lok.GetColumn(1), sz = lok.GetColumn(2);
                        Vector3 sc = new Vector3(sx.magnitude, sy.magnitude, sz.magnitude);
                        Quaternion rot = Quaternion.LookRotation(sz, sy);
                        bool yeni = !pozVar;
                        PozKaydet3(pos, rot, sc);
                        if (yeni) Logger.LogInfo("Local flag pose learned: " + pos + " " + rot.eulerAngles + " scale=" + sc);
                    }
                }
                // gercek kozmetik takiliysa kopyaya gerek yok
                if (yerelKaynak.gameObject.activeInHierarchy) { YerelKaldir(); return; }
            }

            if (yerelKopya != null) return;
            if (yerelSlot == null) { durum = Dil("Pride Flag not found on your gorilla. Equip it once, then unequip it."); return; }

            if (yerelMat == null && ara)
            {
                Material src = KaynakMateryal();
                if (src != null)
                {
                    yerelMat = new Material(src);
                    yerelMat.name = "FlagPrideStandard_Local";
                    if (yerelMat.HasProperty("_BaseColor")) yerelMat.SetColor("_BaseColor", Color.white);
                    if (yerelMat.HasProperty("_Color")) yerelMat.SetColor("_Color", Color.white);
                    yerelMat.SetFloat("_BaseMap_AtlasSlice", 276f);
                }
            }
            if (yerelMat == null) return;
            GomuluSablonYukle();

            yerelKopya = new GameObject("GorillaFlag_Local");
            yerelKopya.layer = yerelSlot.gameObject.layer;
            yerelKopya.transform.SetParent(yerelSlot, false);

            yerelMesh = GomuluMesh();
            yerelBoing = false;
            try { yerelBoing = IskeletKur(yerelSlot.gameObject.layer); }
            catch (Exception ex) { Logger.LogWarning("Boing setup failed, using simple wave: " + ex.Message); yerelBoing = false; }
            if (!yerelBoing)
            {
                if (yerelRgo != null) Destroy(yerelRgo.gameObject);
                GameObject rgo = new GameObject("FlagPrideStandard_FBX");
                rgo.layer = yerelSlot.gameObject.layer;
                rgo.transform.SetParent(yerelKopya.transform, false);
                yerelRgo = rgo.transform;
                rgo.AddComponent<MeshFilter>().sharedMesh = yerelMesh;
                MeshRenderer mr = rgo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = new Material(yerelMat);
            }

            // sap (atlasta x < 81) sabit kalir; kumas sapa uzakligina gore dalgalanir
            yerelTaban = yerelMesh.vertices;
            yerelCalisma = (Vector3[])yerelTaban.Clone();
            Vector2[] uv = yerelMesh.uv;
            float sapX = 0f, sapZ = 0f; int sapN = 0;
            yerelSapAltY = float.MaxValue; yerelSapUstY = float.MinValue;
            for (int i = 0; i < yerelTaban.Length && i < uv.Length; i++)
                if (uv[i].x * 256f < 81f)
                {
                    sapX += yerelTaban[i].x; sapZ += yerelTaban[i].z; sapN++;
                    yerelSapAltY = Mathf.Min(yerelSapAltY, yerelTaban[i].y);
                    yerelSapUstY = Mathf.Max(yerelSapUstY, yerelTaban[i].y);
                }
            yerelSapX = sapN > 0 ? sapX / sapN : 0f;
            yerelSapZ = sapN > 0 ? sapZ / sapN : 0f;
            if (sapN == 0) { yerelSapAltY = 0f; yerelSapUstY = 1f; }
            yerelMesafe = new float[yerelTaban.Length];
            yerelMaks = 0.0001f;
            for (int i = 0; i < yerelTaban.Length; i++)
            {
                bool sap = i < uv.Length && uv[i].x * 256f < 81f;
                yerelMesafe[i] = sap ? 0f : Mathf.Abs(yerelTaban[i].x - yerelSapX);
                yerelMaks = Mathf.Max(yerelMaks, yerelMesafe[i]);
            }

            tutuluyor = false;
            tutanEl = null;
            oncekiVar = false;
            sapli = false; yerde = false; sapliArac = false; sapliHedef = null; ucuyor = false;
            SirtaKoy();
            durum = "";
            if (Etkin) { foreach (var kv in kayitlar) kv.Value.uygulanan = ""; BayragiUygula(); }
            Logger.LogInfo("Local flag created on " + Yol(yerelSlot) + (pozVar ? " pose=" + kayitPos + " " + kayitRot.eulerAngles + " scale=" + kayitOlcek : " (default pose)"));
        }

        // ---------- Oyundaki bayragin birebir kopyasi: ayni kemik zinciri + oyunun BoingBones yay fizigi ----------
        private bool yerelBoing;
        private Component yerelBoingComp;
        private int yerelDonus;

        // Rotation degisince sadece iskeleti yeniden kur: bayrak yerinde kalir (sirtta / elde / sapli), kumas yeni aciyla doner
        private void YerelDonusUygula()
        {
            if (!yerelBoing || yerelKopya == null || yerelRgo == null || yerelMesh == null || yerelMat == null) return;
            string boyImza = BoyImza();
            if (yerelDonus == donus && yerelBoyImza == boyImza) return;
            Type tBones = Type.GetType("BoingKit.BoingBones, BoingKit");
            if (tBones == null) return;
            if (yerelBoyImza != boyImza)
            {
                // sap uzunlugu / bayrak boyutu degisti: modeli yeniden uret (bayrak yerinde kalir, sapin alt ucu ayni)
                Destroy(yerelMesh);
                yerelMesh = FizikMesh(YerelEk, YerelOl, YerelKalin);
                YerelSapOlc();
                yerelBoyImza = boyImza;
                yerelAyakBoy = -1f;
            }
            Vector3 wp = yerelRgo.position; Quaternion wr = yerelRgo.rotation; Vector3 ls = yerelRgo.localScale;
            Vector3 lp = yerelRgo.localPosition; Quaternion lr = yerelRgo.localRotation;
            int katman = yerelRgo.gameObject.layer;
            Destroy(yerelRgo.gameObject);
            Component boing; Transform a, f; SkinnedMeshRenderer smr;
            Transform root = IskeletOlustur(tBones, yerelKopya.transform, katman, yerelMesh, yerelMat,
                r => { r.localPosition = lp; r.localRotation = lr; r.localScale = ls; }, out boing, out a, out f, out smr, donus, YerelEk, YerelOl);
            yerelRgo = root;
            yerelBoingComp = boing;
            yerelKemikA = a; yerelKemikF = f;
            agirMod = false;
            yerelDonus = donus;
            foreach (var kv in kayitlar) kv.Value.uygulanan = "";
            if (Etkin) BayragiUygula();
        }
        private Transform yerelKemikA, yerelKemikF;

        // ---- Saplanmis bayrakta ruzgar + elle dokunma ----
        // Boing fizigi sadece hareketle tepki verdigi icin sabit bayrakta zincire yavas degisen bir "ruzgar" kuvveti veriyoruz.
        private BepInEx.Configuration.ConfigEntry<float> ruzgarCfg;
        private bool agirMod;
        private Vector3 ruzgarYon = Vector3.forward;
        private Vector3 dokunmaKuvvet;
        private readonly Vector3[] elOnceki = new Vector3[2];
        private readonly bool[] elOncekiVar = new bool[2];
        private float ruzgarTohum;
        private bool ucusKumas;

        private void BoingYercekimi(Vector3 g) { BoingYercekimiAyar(yerelBoingComp, g); }

        private static void BoingYercekimiAyar(Component bc, Vector3 g)
        {
            if (bc == null) return;
            Array zincirler = Al(bc, "BoneChains") as Array;
            if (zincirler == null || zincirler.Length == 0) return;
            object ch = zincirler.GetValue(0);
            Ata(ch, "Gravity", g);
            zincirler.SetValue(ch, 0);
        }

        // saplanmis bayrak: daha agir, yavas salinim; sirtta/elde: oyunun orijinal degerleri
        private void BoingAgir(bool agir)
        {
            if (yerelBoingComp == null || agirMod == agir) return;
            agirMod = agir;
            BoingAgirAyar(yerelBoingComp, agir);
        }

        private static void BoingAgirAyar(Component bc, bool agir)
        {
            if (bc == null) return;
            object prm = Al(bc, "Params");
            if (prm == null) return;
            float frek = agir ? 1.1f : 2.5f, yari = agir ? 0.35f : 0.1f;
            Ata(prm, "PositionOscillationFrequency", frek);
            Ata(prm, "RotationOscillationFrequency", frek);
            Ata(prm, "PositionOscillationHalfLife", yari);
            Ata(prm, "RotationOscillationHalfLife", yari);
            Ata(bc, "Params", prm);
        }

        // el hizlari (her karede bir kez): saplanmis bayraklara elle dokunma icin
        private readonly Vector3[] elHiz = new Vector3[2];
        private readonly Vector3[] elPos = new Vector3[2];
        private readonly bool[] elVar = new bool[2];
        private int elKare = -1;
        private void ElHizlariGuncelle()
        {
            if (elKare == Time.frameCount) return;
            elKare = Time.frameCount;
            float dt = Mathf.Max(Time.deltaTime, 0.001f);
            for (int h = 0; h < 2; h++)
            {
                Transform el = El(h);
                if (el == null) { elVar[h] = false; elOncekiVar[h] = false; continue; }
                Vector3 p = el.position;
                Vector3 hz = elOncekiVar[h] ? (p - elOnceki[h]) / dt : Vector3.zero;
                elOnceki[h] = p; elOncekiVar[h] = true;
                if (hz.magnitude > 20f) hz = Vector3.zero;   // isinlanma
                elHiz[h] = hz; elPos[h] = p; elVar[h] = true;
            }
        }

        // yavas, agir ruzgar (zamanla degisen)
        private Vector3 RuzgarKuvvet(float t, Vector3 yon, float carpan = -1f, int ruz = -1)
        {
            if (carpan < 0f) carpan = ruzgarCfg != null ? Mathf.Max(0f, ruzgarCfg.Value) : 1f;
            if (ruz < 0) ruz = ruzgarSeviye;
            Vector3 yan = Vector3.Cross(Vector3.up, yon).normalized;
            float esinti = 0.6f + 0.8f * Mathf.PerlinNoise(t * 0.12f, 1.7f);
            float salin = (Mathf.PerlinNoise(t * 0.28f, 7.3f) - 0.5f) * 2f;
            float dalga = Mathf.Sin(t * 1.1f) * 0.35f;
            Vector3 k = (yon * (2.2f * esinti) + yan * (3.0f * (salin + dalga)) + Vector3.up * (Mathf.Sin(t * 0.6f) * 0.6f)) * carpan;
            if (ruz > 0)
            {
                // ruzgar esiyor: kumasi surekli disa dogru acar, hamlelerle guclenir, hizli cirpinir
                float g = RuzgarGuc[Mathf.Clamp(ruz, 0, 3)];
                float hamle = 0.7f + 0.6f * Mathf.PerlinNoise(t * 0.45f, 3.1f);
                float cirp = (Mathf.PerlinNoise(t * (1.6f + ruz * 0.6f), 9.4f) - 0.5f) * 2f;
                float cirp2 = (Mathf.PerlinNoise(t * (1.3f + ruz * 0.5f), 5.2f) - 0.5f) * 2f;
                k += yon * (g * hamle) + yan * (cirp * g * 0.45f) + Vector3.up * (cirp2 * g * 0.18f);
            }
            return k;
        }

        // ellerin kumasa dokunmasi: verilen itis vektorunu gunceller
        private Vector3 DokunmaGuncelle(Vector3 dokunma, Transform kA, Transform kF)
        {
            float dt = Mathf.Max(Time.deltaTime, 0.001f);
            ElHizlariGuncelle();
            for (int h = 0; h < 2; h++)
            {
                if (!elVar[h]) continue;
                float mesafe = NoktaDogru(elPos[h], kA.position, kF.position);
                if (mesafe < 0.18f && elHiz[h].magnitude > 0.3f)
                {
                    Vector3 it = Vector3.ClampMagnitude(elHiz[h] * 9f, 30f);
                    dokunma = Vector3.Lerp(dokunma, it, Mathf.Clamp01(dt * 12f));
                }
            }
            return dokunma * Mathf.Exp(-dt * 2.5f);
        }

        private void SapliRuzgar()
        {
            if (!yerelBoing || yerelBoingComp == null) return;
            if (ucuyor)
            {
                // havada: kumas hava akimiyla cirpinir
                BoingAgir(false);
                BoingYercekimi(UcusKumasKuvvet(ucusHiz, Time.time + ruzgarTohum));
                ucusKumas = true;
                dokunmaKuvvet = Vector3.zero;
                return;
            }
            if (ucusKumas) { ucusKumas = false; BoingYercekimi(Vector3.zero); }
            if (!sapli || yerde || tasiyan >= 0 || yerelKemikA == null || yerelKemikF == null)
            {
                if (agirMod) { BoingAgir(false); BoingYercekimi(Vector3.zero); }
                dokunmaKuvvet = Vector3.zero;
                return;
            }
            BoingAgir(true);
            Vector3 g = RuzgarKuvvet(Time.time + ruzgarTohum, ruzgarYon);
            dokunmaKuvvet = DokunmaGuncelle(dokunmaKuvvet, yerelKemikA, yerelKemikF);
            BoingYercekimi(g + dokunmaKuvvet);
        }

        // Kumas kemiklerinin (indeks 2..7 = A..F) agirliklari, kumasin 7 sutununda (x = -0.0073 ... -0.4578). Gercek modelden olculdu.
        private static readonly float[] AgirlikX = { -0.00726f, -0.08235f, -0.15744f, -0.23253f, -0.30762f, -0.38271f, -0.4578f };
        private static readonly float[,] AgirlikTablo =
        {
            //  b2      b3      b4      b5      b6      b7
            { 1f,     0f,     0f,     0f,     0f,     0f     },
            { 0.390f, 0.393f, 0.160f, 0.057f, 0f,     0f     },
            { 0.068f, 0.352f, 0.402f, 0.145f, 0.034f, 0f     },
            { 0f,     0.118f, 0.366f, 0.366f, 0.150f, 0f     },
            { 0f,     0f,     0.129f, 0.356f, 0.400f, 0.115f },
            { 0f,     0f,     0.054f, 0.151f, 0.484f, 0.311f },
            { 0f,     0f,     0.045f, 0.133f, 0.548f, 0.274f },
        };

        private static BoneWeight KumasAgirlik(float x)
        {
            float[] w = new float[6];
            int n = AgirlikX.Length;
            if (x >= AgirlikX[0]) { for (int k = 0; k < 6; k++) w[k] = AgirlikTablo[0, k]; }
            else if (x <= AgirlikX[n - 1]) { for (int k = 0; k < 6; k++) w[k] = AgirlikTablo[n - 1, k]; }
            else
            {
                int i = 0;
                while (i < n - 2 && x < AgirlikX[i + 1]) i++;
                float t = (AgirlikX[i] - x) / (AgirlikX[i] - AgirlikX[i + 1]);
                for (int k = 0; k < 6; k++) w[k] = Mathf.Lerp(AgirlikTablo[i, k], AgirlikTablo[i + 1, k], t);
            }
            // en buyuk 4 agirlik
            int[] idx = { 0, 1, 2, 3, 4, 5 };
            Array.Sort(idx, (a, b) => w[b].CompareTo(w[a]));
            float top = w[idx[0]] + w[idx[1]] + w[idx[2]] + w[idx[3]];
            if (top <= 0f) top = 1f;
            BoneWeight bw = new BoneWeight();
            bw.boneIndex0 = idx[0] + 2; bw.weight0 = w[idx[0]] / top;
            bw.boneIndex1 = idx[1] + 2; bw.weight1 = w[idx[1]] / top;
            bw.boneIndex2 = idx[2] + 2; bw.weight2 = w[idx[2]] / top;
            bw.boneIndex3 = idx[3] + 2; bw.weight3 = w[idx[3]] / top;
            return bw;
        }

        private static Transform Kemik(string ad, Transform ust, Vector3 lp, Quaternion lr, int katman)
        {
            GameObject g = new GameObject(ad);
            g.layer = katman;
            g.transform.SetParent(ust, false);
            g.transform.localPosition = lp;
            g.transform.localRotation = lr;
            return g.transform;
        }

        private static void Ata(object o, string ad, object v)
        {
            Type t = o.GetType();
            while (t != null)
            {
                var f = t.GetField(ad, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null)
                {
                    if (v is string && f.FieldType.IsEnum) v = Enum.Parse(f.FieldType, (string)v);
                    f.SetValue(o, v);
                    return;
                }
                t = t.BaseType;
            }
        }

        private static readonly Dictionary<Type, Dictionary<string, System.Reflection.FieldInfo>> alOnbellek = new Dictionary<Type, Dictionary<string, System.Reflection.FieldInfo>>();
        private static object Al(object o, string ad)
        {
            Type t0 = o.GetType();
            Dictionary<string, System.Reflection.FieldInfo> d;
            if (!alOnbellek.TryGetValue(t0, out d)) { d = new Dictionary<string, System.Reflection.FieldInfo>(); alOnbellek[t0] = d; }
            System.Reflection.FieldInfo fi;
            if (d.TryGetValue(ad, out fi)) return fi != null ? fi.GetValue(o) : null;
            d[ad] = AlBul(t0, ad);
            return d[ad] != null ? d[ad].GetValue(o) : null;
        }

        private static System.Reflection.FieldInfo AlBul(Type t, string ad)
        {
            while (t != null)
            {
                var f = t.GetField(ad, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        // Bayrak iskeleti + skinned mesh + oyunun BoingBones fizigi (hem kendi kopyamiz hem diger oyuncularinki icin)
        private Transform IskeletOlustur(Type tBones, Transform ana, int katman, Mesh mesh, Material mat, System.Action<Transform> pozla,
            out Component boingOut, out Transform kemikA, out Transform kemikF, out SkinnedMeshRenderer smrOut, float aci = 0f, float ek = 0f, float ol = 1f)
        {

            // kemikler: Root (= mesh uzayi, sirttaki poz buna uygulanir) / Pole / A..F (oyundaki degerler)
            Transform root = Kemik("Root", ana, Vector3.zero, Quaternion.identity, katman);
            if (pozla != null) pozla(root);
            Transform pole = Kemik("FlagCheckeredPole", root, new Vector3(0f, 0.23095f, 0f), Quaternion.identity, katman);
            // sap uzunlugu / bayrak boyutu: kumas zinciri sapin tepesindeki koseden buyur, sapla yukari kayar
            float aY = 0.62591f - (0.62591f - 0.45971f) * ol + ek - 0.23095f;
            Transform a = Kemik("FlagCheckeredA", pole, new Vector3(0f, aY, 0f), new Quaternion(0.5f, -0.5f, -0.5f, -0.5f), katman);
            Transform b = Kemik("FlagCheckeredB", a, new Vector3(0f, 0.08238f * ol, 0f), Quaternion.identity, katman);
            Transform c = Kemik("FlagCheckeredC", b, new Vector3(0f, 0.07504f * ol, 0f), Quaternion.identity, katman);
            Transform d = Kemik("FlagCheckeredD", c, new Vector3(0f, 0.07518f * ol, 0f), Quaternion.identity, katman);
            Transform e = Kemik("FlagCheckeredE", d, new Vector3(0f, 0.07506f * ol, 0f), Quaternion.identity, katman);
            Transform f = Kemik("FlagCheckeredF", e, new Vector3(0f, 0.07512f * ol, 0f), Quaternion.identity, katman);
            Transform[] kemikler = { root, pole, a, b, c, d, e, f };

            // oyundaki model + agirliklar (birebir), bindpose kemiklerden
            Matrix4x4[] bp = new Matrix4x4[kemikler.Length];
            for (int i = 0; i < kemikler.Length; i++) bp[i] = kemikler[i].worldToLocalMatrix * root.localToWorldMatrix;
            mesh.bindposes = bp;
            // Rotation: kumas sapin etrafinda doner (sap yerinde kalir). Fizik bu duruşu temel alir, birikme olmaz.
            if (aci != 0f) a.localRotation = Quaternion.AngleAxis(aci, Vector3.up) * a.localRotation;

            GameObject rgo = new GameObject("FlagPrideStandard_FBX");
            rgo.layer = katman;
            rgo.transform.SetParent(root, false);
            SkinnedMeshRenderer smr = rgo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = kemikler;
            smr.rootBone = root;
            smr.updateWhenOffscreen = true;
            smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;   // donunce eski yerinde golge kalmasin
            smr.receiveShadows = false;
            smr.sharedMaterial = new Material(mat);
            smrOut = smr;

            // oyunun yay fizigi, gercek bayraktaki ayarlarla
            Component boing = root.gameObject.AddComponent(tBones);
            boingOut = boing;
            kemikA = a; kemikF = f;
            ((Behaviour)boing).enabled = false;
            Type tChain = tBones.GetNestedType("Chain");
            object chain = Activator.CreateInstance(tChain);
            Ata(chain, "Root", a);
            Ata(chain, "Exclusion", new Transform[0]);
            Ata(chain, "EffectorReaction", false);
            Ata(chain, "LooseRoot", false);
            Ata(chain, "AnimationBlendCurveType", "RootOneTailZero");
            Ata(chain, "LengthStiffnessCurveType", "ConstantOne");
            Ata(chain, "PoseStiffnessCurveType", "RootOneTailHalf");
            Ata(chain, "MaxBendAngleCap", 30f);
            Ata(chain, "BendAngleCapCurveType", "ConstantOne");
            Ata(chain, "MaxCollisionRadius", 0.1f);
            Ata(chain, "CollisionRadiusCurveType", "ConstantOne");
            Ata(chain, "EnableBoingKitCollision", false);
            Ata(chain, "EnableUnityCollision", false);
            Ata(chain, "EnableInterChainCollision", false);
            Ata(chain, "Gravity", Vector3.zero);
            Ata(chain, "SquashAndStretchCurveType", "ConstantZero");
            Ata(chain, "MaxSquash", 1.1f);
            Ata(chain, "MaxStretch", 2f);
            Array zincirler = Array.CreateInstance(tChain, 1);
            zincirler.SetValue(chain, 0);
            Ata(boing, "BoneChains", zincirler);

            Ata(boing, "UpdateMode", "EarlyUpdate");
            Ata(boing, "EnablePositionEffect", true);
            Ata(boing, "EnableRotationEffect", true);
            Ata(boing, "EnableScaleEffect", false);
            Ata(boing, "GlobalReactionUpVector", true);
            object prm = Al(boing, "Params");
            if (prm != null)
            {
                object bits = Al(prm, "Bits");
                if (bits != null) { Ata(bits, "m_bits", 1096); Ata(prm, "Bits", bits); }
                Ata(prm, "PositionParameterMode", "OscillationByHalfLife");
                Ata(prm, "RotationParameterMode", "OscillationByHalfLife");
                Ata(prm, "ScaleParameterMode", "OscillationByHalfLife");
                Ata(prm, "PositionExponentialHalfLife", 0.02f);
                Ata(prm, "PositionOscillationHalfLife", 0.1f);
                Ata(prm, "PositionOscillationFrequency", 2.5f);
                Ata(prm, "PositionOscillationDampingRatio", 0.5f);
                Ata(prm, "MoveReactionMultiplier", 1f);
                Ata(prm, "LinearImpulseMultiplier", 1f);
                Ata(prm, "RotationExponentialHalfLife", 0.02f);
                Ata(prm, "RotationOscillationHalfLife", 0.1f);
                Ata(prm, "RotationOscillationFrequency", 2.5f);
                Ata(prm, "RotationOscillationDampingRatio", 0.5f);
                Ata(prm, "RotationReactionMultiplier", 1f);
                Ata(prm, "AngularImpulseMultiplier", 1f);
                Ata(prm, "RotationReactionUp", Vector3.up);
                Ata(boing, "Params", prm);
            }
            ((Behaviour)boing).enabled = true;
            return root;
        }

        private bool IskeletKur(int katman)
        {
            Type tBones = Type.GetType("BoingKit.BoingBones, BoingKit");
            if (tBones == null) { Logger.LogWarning("BoingKit not found."); return false; }
            if (detay <= 6 && GomuluBI.Length != GomuluV.Length / 3 * 4) throw new Exception("weight table size");
            if (yerelMesh != null) Destroy(yerelMesh);
            yerelMesh = FizikMesh(YerelEk, YerelOl, YerelKalin);
            Component boing; Transform a, f; SkinnedMeshRenderer smr;
            IskeletOlustur(tBones, yerelKopya.transform, katman, yerelMesh, yerelMat, root => { yerelRgo = root; SirtaKoy(); },
                out boing, out a, out f, out smr, donus, YerelEk, YerelOl);
            yerelDonus = donus;
            yerelBoyImza = BoyImza();
            yerelBoingComp = boing;
            yerelKemikA = a; yerelKemikF = f;
            agirMod = false;
            Logger.LogInfo("Local flag: BoingBones physics attached.");
            return true;
        }

        private void SirtaKoy()
        {
            if (yerelRgo == null || sapli) return;
            if (sirtSol)
            {
                yerelRgo.localPosition = SolPos;
                yerelRgo.localRotation = SolRot;
                yerelRgo.localScale = kayitOlcek;
            }
            else if (pozVar)
            {
                yerelRgo.localPosition = kayitPos;
                yerelRgo.localRotation = kayitRot;
                yerelRgo.localScale = kayitOlcek;
            }
            else
            {
                yerelRgo.localPosition = Vector3.zero;
                yerelRgo.localRotation = Quaternion.identity;
                yerelRgo.localScale = Vector3.one;
            }
        }


        // =====================================================================
        // SAP MENUSU: sapin, kumasin tam ters tarafinda (180°) kucuk kare dugme. Basinca yaninda menu acilir:
        // sap uzunlugu, bayrak boyutu (en fazla sap kadar), denge ayagi. Dugme ve menu sadece sana gorunur,
        // bayrak elde ya da sapliyken cikar, bayragin donusuyle birlikte doner. Boyutlari ve ayagi herkes gorur.
        // =====================================================================
        private const float BirimM = 0.9f;                 // model biriminin metre karsiligi (bayrak 0.9 olcekli)
        private const float SapOrjMesh = 0.80314f;         // orijinal sap boyu (model birimi)
        private const float KumasOrjMesh = 0.34046f;       // orijinal kumas yuksekligi (model birimi)
        private const float SapVarsayilanM = SapOrjMesh * BirimM;      // 0.72 m
        private const float BayrakVarsayilanM = KumasOrjMesh * BirimM; // 0.31 m
        private const float SapEnUzunM = 6f;
        private const float BayrakEnKucukM = 0.15f;
        private const float KumasAltiPayM = 0.40f;         // kumasin altinda kalan en kisa sap
        private float sapBoyM = SapVarsayilanM, bayrakBoyM = BayrakVarsayilanM;
        private float sapKalin = 1f;                      // sap kalinligi (kat)
        private BepInEx.Configuration.ConfigEntry<float> sapKalinCfg;
        private const float SapCapCm = 0.0137f * 2f * BirimM * 100f;   // orijinal sap capi (~2.5 cm)
        private const float SapKalinMin = 0.75f;
        private float SapKalinMaks { get { return Mathf.Clamp(1f + (sapBoyM - SapVarsayilanM) * 1.5f, 1f, 6f); } }   // uzun sap daha kalin olabilir
        private bool dengeAyak = false;
        private BepInEx.Configuration.ConfigEntry<float> sapBoyCfg, bayrakBoyCfg;
        private BepInEx.Configuration.ConfigEntry<bool> dengeAyakCfg;
        private string yerelBoyImza = "";

        private float YerelEk { get { return Mathf.Max(0f, sapBoyM / BirimM - SapOrjMesh); } }
        private float YerelOl { get { return bayrakBoyM / BayrakVarsayilanM; } }
        private float YerelKalin { get { return sapKalin; } }
        private string BoyImza() { return F(YerelEk) + "/" + F(YerelOl) + "/" + F(YerelKalin); }

        // dalgalanma seviyesi: sapli bayraktaki ruzgar (1 = varsayilan). Kemik zinciri bayrakla buyudugu icin boyla orantili dalgalanir.
        private float DalgaSeviye { get { return ruzgarCfg != null ? Mathf.Clamp(ruzgarCfg.Value, 0f, 3f) : 1f; } }
        private void DalgaDegis(int yon)
        {
            if (ruzgarCfg == null) return;
            float v = DalgaSeviye;
            v = yon > 0 ? (Mathf.Floor(v / 0.25f + 0.001f) + 1f) * 0.25f : (Mathf.Ceil(v / 0.25f - 0.001f) - 1f) * 0.25f;
            ruzgarCfg.Value = Mathf.Clamp(v, 0f, 3f);
        }

        private void SapKalinDegis(int yon)
        {
            float v;
            if (yon > 0) v = (Mathf.Floor(sapKalin / 0.25f + 0.001f) + 1f) * 0.25f;
            else v = (Mathf.Ceil(sapKalin / 0.25f - 0.001f) - 1f) * 0.25f;
            sapKalin = Mathf.Clamp(v, SapKalinMin, SapKalinMaks);
            BoyKaydet();
        }
        private float BayrakEnBuyukM { get { return Mathf.Max(BayrakVarsayilanM, sapBoyM - KumasAltiPayM); } }

        private void SapBoyDegis(int yon)
        {
            float v;
            if (yon > 0) v = (Mathf.Floor(sapBoyM / 0.25f + 0.001f) + 1f) * 0.25f;
            else v = (Mathf.Ceil(sapBoyM / 0.25f - 0.001f) - 1f) * 0.25f;
            sapBoyM = Mathf.Clamp(v, SapVarsayilanM, SapEnUzunM);
            bayrakBoyM = Mathf.Min(bayrakBoyM, BayrakEnBuyukM);
            sapKalin = Mathf.Min(sapKalin, SapKalinMaks);
            BoyKaydet();
        }

        private void BayrakBoyDegis(int yon)
        {
            float v;
            if (yon > 0) v = (Mathf.Floor(bayrakBoyM / 0.1f + 0.001f) + 1f) * 0.1f;
            else v = (Mathf.Ceil(bayrakBoyM / 0.1f - 0.001f) - 1f) * 0.1f;
            bayrakBoyM = Mathf.Clamp(v, BayrakEnKucukM, BayrakEnBuyukM);
            BoyKaydet();
        }

        private void DengeAyakSec(bool a) { dengeAyak = a; if (dengeAyakCfg != null) dengeAyakCfg.Value = a; }

        // ---- GF Sandbox 2. sayfa: ruzgar, dimdik, kilit; ayarlar: firlatma ----
        private int ruzgarSeviye = 0;                                   // 0 yok, 1 hafif, 2 orta, 3 sert
        private BepInEx.Configuration.ConfigEntry<int> ruzgarSevCfg;
        private static readonly string[] RuzgarAd = { "Off", "Gentle", "Medium", "Strong" };
        private static readonly float[] RuzgarGuc = { 0f, 4.5f, 9f, 15f };
        private bool dikAcik = false;                                   // saplarken sap dimdik (dunya yukarisi)
        private BepInEx.Configuration.ConfigEntry<bool> dikCfg;
        private bool kilit = false;                                     // saplanmis bayragi kimse alamaz (sen de)
        private BepInEx.Configuration.ConfigEntry<bool> kilitCfg;
        private bool firlatAcik = true;                                 // bayragi firlatma / havada yakalama
        private BepInEx.Configuration.ConfigEntry<bool> firlatCfg;
        private int sapSayfa = 0;                                       // GF Sandbox sayfasi (0 / 1)

        private void RuzgarDegis(int yon) { ruzgarSeviye = Mathf.Clamp(ruzgarSeviye + yon, 0, 3); if (ruzgarSevCfg != null) ruzgarSevCfg.Value = ruzgarSeviye; }
        private void KilitSec(bool a) { kilit = a; if (kilitCfg != null) kilitCfg.Value = a; }
        private void FirlatSec(bool a) { firlatAcik = a; if (firlatCfg != null) firlatCfg.Value = a; }
        private void DikSec(bool a)
        {
            dikAcik = a; if (dikCfg != null) dikCfg.Value = a;
            if (a && sapli && !yerde && tasiyan < 0) DikDuzelt();   // acinca saplanmis bayrak hemen dikilir
        }

        // saplanmis bayragi sapin alt ucu etrafinda dondurup dimdik yap
        private void DikDuzelt()
        {
            if (yerelRgo == null) return;
            Vector3 alt = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapAltY, yerelSapZ));
            Vector3 ust = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapUstY, yerelSapZ));
            Vector3 eksen = ust - alt;
            if (eksen.sqrMagnitude < 1e-6f) return;
            Quaternion d = Quaternion.FromToRotation(eksen.normalized, Vector3.up);
            yerelRgo.rotation = d * yerelRgo.rotation;
            yerelRgo.position = alt + d * (yerelRgo.position - alt);
            if (sapliHedef != null)
            {
                sapliLokPos = sapliHedef.InverseTransformPoint(yerelRgo.position);
                sapliLokRot = Quaternion.Inverse(sapliHedef.rotation) * yerelRgo.rotation;
            }
            if (yerelKemikA != null && yerelKemikF != null)
            {
                Vector3 kd = yerelKemikF.position - yerelKemikA.position; kd.y = 0f;
                if (kd.sqrMagnitude > 1e-6f) ruzgarYon = kd.normalized;
            }
        }

        private void BoyKaydet()
        {
            if (sapBoyCfg != null) sapBoyCfg.Value = sapBoyM;
            if (bayrakBoyCfg != null) bayrakBoyCfg.Value = bayrakBoyM;
            if (sapKalinCfg != null) sapKalinCfg.Value = sapKalin;
            donusDirty = true;      // iskelet yeni boyla yeniden kurulsun
        }

        private void BoySifirla()
        {
            sapBoyM = SapVarsayilanM; bayrakBoyM = BayrakVarsayilanM; sapKalin = 1f;
            if (ruzgarCfg != null) ruzgarCfg.Value = 1f;
            BoyKaydet();
            DengeAyakSec(false);
            RuzgarDegis(-3);
            dikAcik = false; if (dikCfg != null) dikCfg.Value = false;
            KilitSec(false);
        }

        // modeli uzat / buyut: sap yukari uzar (alt ucu yerinde kalir), kumas sapin tepesindeki koseden buyur
        private static void BoyUygula(Mesh m, float ek, float ol, float kal = 1f)
        {
            if (m == null || (ek <= 0.0001f && Mathf.Abs(ol - 1f) < 0.0001f && Mathf.Abs(kal - 1f) < 0.0001f)) return;
            const float HX = -0.00726f, HY = 0.62591f;
            Vector3[] v = m.vertices; Vector2[] uv = m.uv;
            for (int i = 0; i < v.Length; i++)
            {
                bool sap = i < uv.Length && uv[i].x * 256f < 81f;
                if (sap) { if (v[i].y > 0.4f) v[i].y += ek; v[i].x *= kal; v[i].z *= kal; }
                else { v[i].x = HX + (v[i].x - HX) * ol; v[i].y = HY + (v[i].y - HY) * ol + ek; }
            }
            m.vertices = v;
            m.RecalculateBounds();
        }

        private void YerelSapOlc()
        {
            if (yerelMesh == null) return;
            Vector3[] v = yerelMesh.vertices; Vector2[] uv = yerelMesh.uv;
            float sx = 0f, sz = 0f; int n = 0;
            yerelSapAltY = float.MaxValue; yerelSapUstY = float.MinValue;
            for (int i = 0; i < v.Length && i < uv.Length; i++)
                if (uv[i].x * 256f < 81f)
                {
                    sx += v[i].x; sz += v[i].z; n++;
                    yerelSapAltY = Mathf.Min(yerelSapAltY, v[i].y);
                    yerelSapUstY = Mathf.Max(yerelSapUstY, v[i].y);
                }
            yerelSapX = n > 0 ? sx / n : 0f; yerelSapZ = n > 0 ? sz / n : 0f;
            if (n == 0) { yerelSapAltY = 0f; yerelSapUstY = 1f; }
        }

        // ---------- denge ayagi: sapli bayragin dibinde, sapla ayni renkte yuvarlak kaide ----------
        private GameObject yerelAyak;
        private float yerelAyakBoy = -1f;
        private bool yerelAyakYerde;
        private Renderer yerelAyakKaynak;

        // sapin dokusundaki koyu cizgiyi (atlasta x 16-20) yandaki ahsapla doldur
        private static void SapKarartiSil(Color32[] px, int w)
        {
            if (px == null || px.Length < w * w) return;
            for (int y = 10; y < 246; y++)
            {
                List<float> l = new List<float>();
                for (int x = 14; x < 40; x++) { Color32 c = px[y * w + x]; l.Add((c.r + c.g + c.b) / 3f); }
                l.Sort();
                float med = l[l.Count / 2];
                for (int x = 14; x < 40; x++)
                {
                    Color32 c = px[y * w + x];
                    if ((c.r + c.g + c.b) / 3f >= med * 0.6f) continue;
                    int k = x + 6 < 40 ? x + 6 : x - 6;
                    px[y * w + x] = px[y * w + k];
                }
            }
        }

        private static Mesh AyakMesh(Mesh bayrak, float sapM)
        {
            // sapin dokusundan (atlas x < 81) renk alanini bul
            float u0 = 0.1f, u1 = 0.2f, v0 = 0.1f, v1 = 0.9f;
            if (bayrak != null)
            {
                Vector2[] uvs = bayrak.uv;
                float a = float.MaxValue, b = float.MinValue, c = float.MaxValue, d = float.MinValue;
                foreach (Vector2 t in uvs)
                    if (t.x * 256f < 81f) { a = Mathf.Min(a, t.x); b = Mathf.Max(b, t.x); c = Mathf.Min(c, t.y); d = Mathf.Max(d, t.y); }
                if (a < b) { u0 = a; u1 = b; v0 = c; v1 = d; }
            }
            float rp = 0.014f;                                               // sap yaricapi (metre)
            if (bayrak != null)
            {
                Vector3[] bv = bayrak.vertices; Vector2[] bu = bayrak.uv; float mr = 0f;
                for (int i = 0; i < bv.Length && i < bu.Length; i++) if (bu[i].x * 256f < 81f) mr = Mathf.Max(mr, new Vector2(bv[i].x, bv[i].z).magnitude);
                if (mr > 0f) rp = mr * BirimM + 0.002f;
            }
            float R = Mathf.Max(Mathf.Clamp(0.05f + 0.045f * sapM, 0.07f, 0.35f), rp * 3.5f);   // yaricap
            float h1 = R * 0.35f, h2 = R * 0.3f;                             // duvar, cati
            const int N = 32;
            List<Vector3> v = new List<Vector3>(); List<Vector2> uv = new List<Vector2>(); List<int> tri = new List<int>();
            // halkalar: (yaricap, yukseklik)
            Vector2[] halka = { new Vector2(R, 0f), new Vector2(R, h1), new Vector2(R, h1), new Vector2(rp, h1 + h2) };
            for (int s = 0; s < halka.Length; s++)
                for (int i = 0; i <= N; i++)
                {
                    float a = i / (float)N * Mathf.PI * 2f;
                    v.Add(new Vector3(Mathf.Cos(a) * halka[s].x, halka[s].y, Mathf.Sin(a) * halka[s].x));
                    // tek renk: sapin dibindeki dokudan kucuk bir nokta (desenler halka halka bozulmasin)
                    uv.Add(SapUV(u0, u1, v0, v1, 0.04f, i / (float)N));
                }
            for (int s = 0; s < halka.Length - 1; s++)
            {
                if (s == 1) continue;   // duvar ile cati arasi (ayni halka, keskin kenar)
                for (int i = 0; i < N; i++)
                {
                    int a = s * (N + 1) + i, b = a + 1, c = a + N + 1, d = c + 1;
                    tri.AddRange(new[] { a, c, b, b, c, d });
                }
            }
            Mesh m = new Mesh();
            m.name = "GorillaFlag_BaseStand";
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tri, 0);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        // sap dokusunda, verilen yukseklikte (0 alt, 1 ust) kucuk bir alan
        private static Vector2 SapUV(float u0, float u1, float v0, float v1, float yukseklik, float t)
        {
            float uc = (u0 + u1) * 0.5f, du = (u1 - u0) * 0.04f;
            float v = Mathf.Lerp(v0, v1, yukseklik);
            return new Vector2(uc + (t - 0.5f) * du, v);
        }

        private static void SapUVAlani(Mesh bayrak, out float u0, out float u1, out float v0, out float v1)
        {
            u0 = 0.1f; u1 = 0.2f; v0 = 0.1f; v1 = 0.9f;
            if (bayrak == null) return;
            float a = float.MaxValue, b = float.MinValue, c = float.MaxValue, d = float.MinValue;
            foreach (Vector2 t in bayrak.uv)
                if (t.x * 256f < 81f) { a = Mathf.Min(a, t.x); b = Mathf.Max(b, t.x); c = Mathf.Min(c, t.y); d = Mathf.Max(d, t.y); }
            if (a < b) { u0 = a; u1 = b; v0 = c; v1 = d; }
        }

        private static GameObject AyakOlustur(Mesh bayrak, float sapM, int katman)
        {
            GameObject g = new GameObject("GorillaFlag_BaseStand");
            g.layer = katman;
            g.AddComponent<MeshFilter>().sharedMesh = AyakMesh(bayrak, sapM);
            MeshRenderer mr = g.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return g;
        }

        private static void AyakSil(ref GameObject g)
        {
            if (g == null) return;
            MeshFilter mf = g.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
            Destroy(g);
            g = null;
        }

        // ayagi sapin dibindeki zemine oturt (sapin ekseni yonunde)
        private void AyakYerlestir(GameObject g, Vector3 alt, Vector3 ust)
        {
            Vector3 eksen = (ust - alt).normalized;
            if (eksen.sqrMagnitude < 0.5f) eksen = Vector3.up;
            RaycastHit h;
            Vector3 yer = alt + eksen * 0.08f;
            if (ZeminBul(alt + eksen * 0.5f, -eksen, 1.2f, out h)) yer = h.point;
            g.transform.position = yer;
            g.transform.rotation = Quaternion.FromToRotation(Vector3.up, eksen);
        }

        private void YerelAyakKare()
        {
            bool goster = dengeAyak && sapli && !yerde && tasiyan < 0 && yerelKopya != null && yerelRgo != null && yerelKopya.activeInHierarchy;
            if (!goster)
            {
                if (yerelAyak != null) yerelAyak.SetActive(false);
                yerelAyakYerde = false;
                return;
            }
            Vector3 alt = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapAltY, yerelSapZ));
            Vector3 ust = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapUstY, yerelSapZ));
            float boy = Mathf.Round(Vector3.Distance(alt, ust) * 20f) / 20f;
            if (yerelAyak == null || Mathf.Abs(boy - yerelAyakBoy) > 0.01f)
            {
                AyakSil(ref yerelAyak);
                yerelAyak = AyakOlustur(yerelMesh, boy, yerelRgo.gameObject.layer);
                yerelAyakBoy = boy;
                yerelAyakYerde = false;
            }
            if (yerelAyakKaynak == null || !yerelAyakKaynak.transform.IsChildOf(yerelRgo))
            {
                yerelAyakKaynak = yerelRgo.GetComponentInChildren<SkinnedMeshRenderer>();
                if (yerelAyakKaynak == null) yerelAyakKaynak = yerelRgo.GetComponentInChildren<Renderer>();
            }
            Renderer br = yerelAyakKaynak;
            MeshRenderer mr = yerelAyak.GetComponent<MeshRenderer>();
            if (br != null && mr.sharedMaterial != br.sharedMaterial) mr.sharedMaterial = br.sharedMaterial;
            if (!yerelAyakYerde) { AyakYerlestir(yerelAyak, alt, ust); yerelAyak.transform.SetParent(yerelRgo, true); yerelAyakYerde = true; }
            if (!yerelAyak.activeSelf) yerelAyak.SetActive(true);
        }

        private void UzakAyakKare()
        {
            foreach (var kv in uzaklar)
            {
                Uzak u = kv.Value;
                bool goster = u.ayak && u.durum == 3 && u.kopya != null && u.rgo != null && u.sapVar && u.kopya.activeInHierarchy;
                if (!goster)
                {
                    if (u.ayakGo != null) u.ayakGo.SetActive(false);
                    u.ayakYerde = false;
                    continue;
                }
                Vector3 alt, ust; UzakSapUclari(u, out alt, out ust);
                float boy = Mathf.Round(Vector3.Distance(alt, ust) * 20f) / 20f;
                if (u.ayakGo == null || Mathf.Abs(boy - u.ayakBoy) > 0.01f)
                {
                    AyakSil(ref u.ayakGo);
                    u.ayakGo = AyakOlustur(u.mesh, boy, u.kopya.layer);
                    u.ayakBoy = boy;
                    u.ayakYerde = false;
                }
                MeshRenderer mr = u.ayakGo.GetComponent<MeshRenderer>();
                Material m = u.kayit != null && u.kayit.mat != null ? u.kayit.mat : (u.smr != null ? u.smr.sharedMaterial : null);
                if (m != null && mr.sharedMaterial != m) mr.sharedMaterial = m;
                if (!u.ayakYerde || u.degisti) { u.ayakGo.transform.SetParent(null, true); AyakYerlestir(u.ayakGo, alt, ust); u.ayakGo.transform.SetParent(u.rgo, true); u.ayakYerde = true; }
                if (!u.ayakGo.activeSelf) u.ayakGo.SetActive(true);
            }
        }

        // ---------- sap dugmesi + menu (sadece sana gorunur) ----------
        private const float MW = 300f, MH = 512f;
        private const float MOlcek = 0.32f / 300f;        // menu 32 cm genis
        private const float DugmeM = 0.0175f;             // dugme 1.75 cm
        private const float DugmeKalin = 0.009f;          // dugmenin kalinligi (sapa yapisik ince kare)
        private GameObject sapDugme;
        private Material sapDugmeMat;
        private GameObject sapMenu;
        private Mesh sapDugmeUVMesh;
        private Mesh sapDugmeUVKaynak;
        private bool sapDugmeKamuflaj;
        private MaterialPropertyBlock sapDugmeBlok;

        // dugmeye sapin dokusunu giydir: kup mesh'inin tum UV'leri sapin dugme hizasindaki kucuk alana
        private void DugmeDokusu()
        {
            if (sapDugme == null || yerelRgo == null) return;
            if (yerelAyakKaynak == null || !yerelAyakKaynak.transform.IsChildOf(yerelRgo))
            {
                yerelAyakKaynak = yerelRgo.GetComponentInChildren<SkinnedMeshRenderer>();
                if (yerelAyakKaynak == null) yerelAyakKaynak = yerelRgo.GetComponentInChildren<Renderer>();
            }
            MeshRenderer mr = sapDugme.GetComponent<MeshRenderer>();
            MeshFilter mf = sapDugme.GetComponent<MeshFilter>();
            if (yerelAyakKaynak == null || yerelAyakKaynak.sharedMaterial == null || yerelMesh == null || mf == null)
            {
                sapDugmeKamuflaj = false;
                if (sapDugmeMat != null && mr.sharedMaterial != sapDugmeMat) { mr.sharedMaterial = sapDugmeMat; mr.SetPropertyBlock(null); }
                return;
            }
            if (sapDugmeUVMesh == null || sapDugmeUVKaynak != yerelMesh)
            {
                float u0, u1, v0, v1;
                SapUVAlani(yerelMesh, out u0, out u1, out v0, out v1);
                Mesh kup = mf.sharedMesh;
                Mesh m = Instantiate(kup);
                m.name = "GorillaFlag_PoleButtonMesh";
                Vector2[] uv = new Vector2[m.vertexCount];
                float yuk = Mathf.InverseLerp(-0.1622f, 0.64094f, 0.64094f - 0.06f);   // dugmenin sapta durdugu yukseklik
                for (int i = 0; i < uv.Length; i++) uv[i] = SapUV(u0, u1, v0, v1, yuk, (i % 4) / 3f);
                m.uv = uv;
                if (sapDugmeUVMesh != null) Destroy(sapDugmeUVMesh);
                sapDugmeUVMesh = m;
                sapDugmeUVKaynak = yerelMesh;
                mf.sharedMesh = m;
            }
            if (mr.sharedMaterial != yerelAyakKaynak.sharedMaterial) mr.sharedMaterial = yerelAyakKaynak.sharedMaterial;
            sapDugmeKamuflaj = true;
        }
        private RectTransform sapMenuIcerik;
        private bool sapMenuAcik;
        private bool sandboxAcik = true;
        private BepInEx.Configuration.ConfigEntry<bool> sandboxCfg;
        private void SandboxSec(bool a) { sandboxAcik = a; if (sandboxCfg != null) sandboxCfg.Value = a; if (!a) sapMenuAcik = false; }
        private string sapMenuImza = "";
        private readonly List<KeyValuePair<Rect, Action>> mTiklar = new List<KeyValuePair<Rect, Action>>();
        private readonly bool[] dBasili = new bool[4], dHazir = new bool[4], mBasili = new bool[8], mHazir = new bool[8];
        private float mTikZaman, dBasZaman;
        private bool mFareOnceki;

        // bayrak sirta donunce sap uzunlugu / kalinligi / bayrak boyu normale doner (dalgalanma ve ayak ayari kalir)
        private int sapOncekiYer = -1;
        private void SirtBoyKontrol()
        {
            if (yerelKopya == null) return;
            int yer = ucuyor ? 3 : sapli ? 2 : (tutuluyor ? 1 : 0);
            if (yer == 0 && sapOncekiYer != 0 && tasiyan < 0)
            {
                bool farkli = Mathf.Abs(sapBoyM - SapVarsayilanM) > 0.001f || Mathf.Abs(bayrakBoyM - BayrakVarsayilanM) > 0.001f || Mathf.Abs(sapKalin - 1f) > 0.001f;
                if (farkli)
                {
                    sapBoyM = SapVarsayilanM; bayrakBoyM = BayrakVarsayilanM; sapKalin = 1f;
                    BoyKaydet();
                    Logger.LogInfo("Flag back on the back: GF Sandbox size reset to normal.");
                }
            }
            sapOncekiYer = yer;
        }

        private void SapMenuKare()
        {
            SirtBoyKontrol();
            bool goster = sandboxAcik && gizli && yerelKopya != null && yerelRgo != null && yerelKopya.activeInHierarchy
                && sapli && !yerde && tasiyan < 0;   // sadece yere saplanmisken (elde / sirtta gorunmez)
            if (!goster)
            {
                if (sapDugme != null && sapDugme.activeSelf) sapDugme.SetActive(false);
                if (sapMenu != null && sapMenu.activeSelf) sapMenu.SetActive(false);
                sapMenuAcik = false;
                return;
            }

            // dugmenin yeri: kumasin ust kenari hizasinda, sapin kumasa ters tarafinda; bayragin donusuyle doner
            Quaternion q = yerelRgo.rotation * Quaternion.AngleAxis(donus, Vector3.up);
            Vector3 dis = q * Vector3.right;
            float s = Mathf.Abs(yerelRgo.lossyScale.y);
            Vector3 eksenNokta = yerelRgo.TransformPoint(new Vector3(yerelSapX, 0.64094f - 0.06f, yerelSapZ));   // sap uzasa da ilk yerinde kalir
            Vector3 dPos = eksenNokta + dis * (0.0137f * YerelKalin * s + DugmeKalin * 0.5f);   // sapin yuzeyine yapisik

            if (sapDugme == null)
            {
                sapDugme = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sapDugme.name = "GorillaFlag_PoleButton";
                Collider c = sapDugme.GetComponent<Collider>();
                if (c != null) Destroy(c);
                sapDugmeMat = YeniDuzMat();
                if (sapDugmeMat != null) sapDugme.GetComponent<MeshRenderer>().sharedMaterial = sapDugmeMat;
                sapDugme.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                sapDugmeUVMesh = null;
            }
            DugmeDokusu();
            if (!sapDugme.activeSelf) sapDugme.SetActive(true);
            bool basik = Time.time - dBasZaman < 0.15f;
            sapDugme.transform.position = dPos - dis * (basik ? DugmeKalin * 0.3f : 0f);
            sapDugme.transform.rotation = q;
            sapDugme.transform.localScale = new Vector3(DugmeKalin, DugmeM, DugmeM);
            if (sapDugmeKamuflaj)
            {
                // sapin malzemesi + dokusu (kamuflaj); basilinca / acikken tema rengine boyanir
                if (sapDugmeBlok == null) sapDugmeBlok = new MaterialPropertyBlock();
                sapDugmeBlok.SetColor("_BaseColor", sapMenuAcik || basik ? EkKirmizi : Color.white);
                sapDugmeBlok.SetColor("_Color", sapMenuAcik || basik ? EkKirmizi : Color.white);
                sapDugme.GetComponent<MeshRenderer>().SetPropertyBlock(sapDugmeBlok);
            }
            else DuzMatRenk(sapDugmeMat, sapMenuAcik || basik ? EkKirmizi : new Color(0.85f, 0.85f, 0.85f, 1f));

            // menu: sapin yaninda, kumasa paralel, dugmeden disa dogru acilir
            if (sapMenuAcik)
            {
                if (sapMenu == null) SapMenuOlustur();
                if (sapMenu != null)
                {
                    if (!sapMenu.activeSelf) sapMenu.SetActive(true);
                    float gen = MW * MOlcek, yuk = MH * MOlcek;
                    Vector3 ust = q * Vector3.up;
                    sapMenu.transform.position = dPos + dis * (DugmeKalin * 0.5f + 0.06f + gen * 0.5f) + ust * (DugmeM * 0.5f - yuk * 0.5f);
                    sapMenu.transform.rotation = q;
                    sapMenu.transform.localScale = Vector3.one * MOlcek;
                    string imza = sapBoyM + "|" + bayrakBoyM + "|" + sapKalin + "|" + DalgaSeviye + "|" + dengeAyak + "|" + temaNo + "|" + ekAydinlik + "|" + dil + "|" + yaziStili
                        + "|" + sapSayfa + "|" + ruzgarSeviye + "|" + dikAcik + "|" + kilit;
                    if (imza != sapMenuImza) { sapMenuImza = imza; SapMenuCiz(); }
                }
            }
            else if (sapMenu != null && sapMenu.activeSelf) sapMenu.SetActive(false);

            SapGirdi(dPos);
        }

        private static Material YeniDuzMat()
        {
            foreach (string a in new[] { "Universal Render Pipeline/Unlit", "Unlit/Color", "Universal Render Pipeline/Simple Lit" })
            {
                Shader sh = Shader.Find(a);
                if (sh != null) return new Material(sh);
            }
            return null;
        }

        private static void DuzMatRenk(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        private bool EkKaynakHazirla()
        {
            Font fs = EkFontSec();
            if (fs != null) ekranFont = fs;
            if (ekranFont == null) { try { ekranFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { ekranFont = null; } }
            if (ekranFont == null) { try { ekranFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { ekranFont = null; } }
            if (ekranFont == null) return false;
            if (ekranYuvarlak == null)
            {
                int n = 32; float rad = 10f;
                Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
                t.wrapMode = TextureWrapMode.Clamp;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float cx = Mathf.Clamp(x + 0.5f, rad, n - rad), cy = Mathf.Clamp(y + 0.5f, rad, n - rad);
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                        t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(rad - d + 0.5f)));
                    }
                t.Apply();
                ekranYuvarlak = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(11, 11, 11, 11));
            }
            return true;
        }

        private void SapMenuOlustur()
        {
            if (!EkKaynakHazirla()) return;
            sapMenu = new GameObject("GorillaFlag_PoleMenu");
            Canvas c = sapMenu.AddComponent<Canvas>();
            c.renderMode = RenderMode.WorldSpace;
            var sc = sapMenu.AddComponent<UnityEngine.UI.CanvasScaler>();
            sc.dynamicPixelsPerUnit = 4f;
            sapMenu.GetComponent<RectTransform>().sizeDelta = new Vector2(MW, MH);
            // arka yuz: duz gri, opak (arkadan bakinca ters yazilar gorunmesin)
            GameObject arka = GameObject.CreatePrimitive(PrimitiveType.Quad);
            arka.name = "GorillaFlag_PoleMenuBack";
            Collider ac = arka.GetComponent<Collider>();
            if (ac != null) Destroy(ac);
            arka.transform.SetParent(sapMenu.transform, false);
            arka.transform.localPosition = new Vector3(0f, 0f, 1.5f);
            arka.transform.localRotation = Quaternion.identity;
            arka.transform.localScale = new Vector3(MW - 8f, MH - 8f, 1f);
            Material am = YeniDuzMat();
            if (am != null)
            {
                DuzMatRenk(am, new Color(0.36f, 0.37f, 0.4f, 1f));
                if (am.HasProperty("_Cull")) am.SetFloat("_Cull", 0f);
                arka.GetComponent<MeshRenderer>().sharedMaterial = am;
            }
            arka.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // ikinci yuz: ayni menu, arkaya donuk (iki taraftan da duz okunur, ayna gibi ters degil)
            GameObject ay = new GameObject("GorillaFlag_PoleMenuBackFace");
            sapMenuArka = ay.AddComponent<RectTransform>();
            sapMenuArka.SetParent(sapMenu.transform, false);
            sapMenuArka.sizeDelta = new Vector2(MW, MH);
            sapMenuArka.localPosition = new Vector3(0f, 0f, 3f);
            sapMenuArka.localRotation = Quaternion.Euler(0f, 180f, 0f);
            sapMenuImza = "";
        }

        private RectTransform sapMenuIcerik2;   // arka yuzun icerigi
        private RectTransform sapMenuArka;      // arka yuz (180° donuk): arkadan bakinca da duz okunur

        private void SapMenuCiz()
        {
            if (sapMenu == null || !EkKaynakHazirla()) return;
            if (sapMenuIcerik != null) Destroy(sapMenuIcerik.gameObject);
            if (sapMenuIcerik2 != null) Destroy(sapMenuIcerik2.gameObject);
            RectTransform eskiIcerik = ekranIcerik;
            List<KeyValuePair<Rect, Action>> eskiTik = new List<KeyValuePair<Rect, Action>>(eTiklar);
            try
            {
                for (int yuz = 0; yuz < 2; yuz++)
                {
                    Transform ana = yuz == 0 ? sapMenu.transform : (sapMenuArka != null ? sapMenuArka.transform : null);
                    if (ana == null) continue;
                    GameObject ic = new GameObject("Icerik");
                    RectTransform r = ic.AddComponent<RectTransform>();
                    r.SetParent(ana, false);
                    r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
                    r.offsetMin = r.offsetMax = Vector2.zero;
                    if (yuz == 0) sapMenuIcerik = r; else sapMenuIcerik2 = r;
                    ekranIcerik = r;
                    eTiklar.Clear();
                    SapMenuIcerikCiz();
                    if (yuz == 0) { mTiklar.Clear(); mTiklar.AddRange(eTiklar); }   // iki yuzun duzeni ayni
                }
            }
            finally
            {
                ekranIcerik = eskiIcerik;
                eTiklar.Clear();
                eTiklar.AddRange(eskiTik);
            }
        }

        private void SapMenuIcerikCiz()
        {
            EkKutuCiz(0, 0, MW, MH, EkKirmizi);
            EkKutuCiz(3, 3, MW - 6, MH - 6, EkEkran);
            EkYazi(18, 12, 220, 28, "GF SANDBOX", 15, EkKirmizi, TextAnchor.MiddleLeft);
            EkDugme(MW - 48, 12, 32, 28, "X", false, () => { sapMenuAcik = false; }, 13);
            EkKutuCiz(16, 46, MW - 32, 2, EkKenar, false);

            // sira: sap uzunlugu, sap kalinligi, bayrak boyu, dimdik | denge ayagi, dalgalanma, ruzgar, kilit
            if (sapSayfa < 0 || sapSayfa > 1) sapSayfa = 0;
            float y = 56;
            if (sapSayfa == 1) { SapMenuSayfa2(y); SapMenuAlt(); return; }

            // sap uzunlugu
            EkYazi(18, y, 264, 18, Dil("Pole length"), 13, EkSoluk, TextAnchor.MiddleLeft, false);
            y += 22;
            EkDugme(16, y, 44, 36, "-", true, () => SapBoyDegis(-1), 20);
            EkKenarli(66, y, 168, 36, EkKoyu, EkKenar);
            EkYazi(66, y, 168, 36, sapBoyM.ToString("0.00", CultureInfo.InvariantCulture) + " m", 17, EkMetin);
            EkDugme(240, y, 44, 36, "+", true, () => SapBoyDegis(1), 20);
            y += 42;
            EkKutuCiz(16, y, 268, 5, EkKenar, false);
            EkKutuCiz(16, y, Mathf.Max(4f, 268f * Mathf.InverseLerp(SapVarsayilanM, SapEnUzunM, sapBoyM)), 5, EkKirmizi, false);

            // sap kalinligi (uzunluga gore en fazla)
            y += 15;
            EkYazi(18, y, 264, 18, Dil("Pole thickness"), 13, EkSoluk, TextAnchor.MiddleLeft, false);
            y += 22;
            EkDugme(16, y, 44, 36, "-", true, () => SapKalinDegis(-1), 20);
            EkKenarli(66, y, 168, 36, EkKoyu, EkKenar);
            EkYazi(66, y, 168, 36, (SapCapCm * sapKalin).ToString("0.0", CultureInfo.InvariantCulture) + " cm", 17, EkMetin);
            EkDugme(240, y, 44, 36, "+", true, () => SapKalinDegis(1), 20);
            y += 42;
            EkKutuCiz(16, y, 268, 5, EkKenar, false);
            EkKutuCiz(16, y, Mathf.Max(4f, 268f * Mathf.InverseLerp(SapKalinMin, 6f, sapKalin)), 5, EkKirmizi, false);
            EkKutuCiz(16 + 268f * Mathf.InverseLerp(SapKalinMin, 6f, SapKalinMaks) - 1f, y - 3, 2, 11, EkSoluk, false);   // bu uzunlukta en fazla

            // bayrak boyutu
            y += 15;
            EkYazi(18, y, 264, 18, Dil("Flag size"), 13, EkSoluk, TextAnchor.MiddleLeft, false);
            y += 22;
            EkDugme(16, y, 44, 36, "-", true, () => BayrakBoyDegis(-1), 20);
            EkKenarli(66, y, 168, 36, EkKoyu, EkKenar);
            EkYazi(66, y, 168, 36, bayrakBoyM.ToString("0.00", CultureInfo.InvariantCulture) + " m", 17, EkMetin);
            EkDugme(240, y, 44, 36, "+", true, () => BayrakBoyDegis(1), 20);
            y += 42;
            EkKutuCiz(16, y, 268, 5, EkKenar, false);
            EkKutuCiz(16, y, Mathf.Max(4f, 268f * Mathf.InverseLerp(BayrakEnKucukM, BayrakEnBuyukM, bayrakBoyM)), 5, EkKirmizi, false);
            y += 8;
            EkYazi(18, y, 264, 16, Dil("Up to the pole length"), 10, EkNot, TextAnchor.MiddleLeft, false);

            // dimdik
            y += 22;
            EkYazi(18, y, 264, 18, Dil("Upright"), 13, EkSoluk, TextAnchor.MiddleLeft, false);
            y += 22;
            EkDugme(16, y, 131, 34, Dil("On"), dikAcik, () => DikSec(true), 14);
            EkDugme(153, y, 131, 34, Dil("Off"), !dikAcik, () => DikSec(false), 14);
            y += 38;
            EkYazi(18, y, 264, 16, Dil("The pole stands straight up when planted."), 10, EkNot, TextAnchor.MiddleLeft, false);

            SapMenuAlt();
        }

        private void SapMenuSayfa2(float y)
        {
            // denge ayagi
            EkYazi(18, y, 264, 18, Dil("Balance base"), 13, EkSoluk, TextAnchor.MiddleLeft, false);
            y += 22;
            EkDugme(16, y, 131, 34, Dil("On"), dengeAyak, () => DengeAyakSec(true), 14);
            EkDugme(153, y, 131, 34, Dil("Off"), !dengeAyak, () => DengeAyakSec(false), 14);

            // dalgalanma
            y += 46;
            EkYazi(18, y, 264, 18, Dil("Wave"), 13, EkSoluk, TextAnchor.MiddleLeft, false);
            y += 22;
            EkDugme(16, y, 44, 36, "-", true, () => DalgaDegis(-1), 20);
            EkKenarli(66, y, 168, 36, EkKoyu, EkKenar);
            EkYazi(66, y, 168, 36, Mathf.RoundToInt(DalgaSeviye * 100f) + "%", 17, EkMetin);
            EkDugme(240, y, 44, 36, "+", true, () => DalgaDegis(1), 20);
            y += 42;
            EkKutuCiz(16, y, 268, 5, EkKenar, false);
            EkKutuCiz(16, y, Mathf.Max(4f, 268f * DalgaSeviye / 3f), 5, EkKirmizi, false);

            // ruzgar
            y += 15;
            EkYazi(18, y, 264, 18, Dil("Wind"), 13, EkSoluk, TextAnchor.MiddleLeft, false);
            y += 22;
            EkDugme(16, y, 44, 36, "-", true, () => RuzgarDegis(-1), 20);
            EkKenarli(66, y, 168, 36, ruzgarSeviye > 0 ? EkKoyuKirmizi : EkKoyu, ruzgarSeviye > 0 ? EkKirmizi : EkKenar);
            EkYazi(66, y, 168, 36, Dil(RuzgarAd[Mathf.Clamp(ruzgarSeviye, 0, 3)]), 16, EkMetin);
            EkDugme(240, y, 44, 36, "+", true, () => RuzgarDegis(1), 20);
            y += 42;
            EkKutuCiz(16, y, 268, 5, EkKenar, false);
            EkKutuCiz(16, y, Mathf.Max(4f, 268f * ruzgarSeviye / 3f), 5, EkKirmizi, false);

            // kilit
            y += 15;
            EkYazi(18, y, 264, 18, Dil("Lock"), 13, EkSoluk, TextAnchor.MiddleLeft, false);
            y += 22;
            EkDugme(16, y, 131, 34, Dil("On"), kilit, () => KilitSec(true), 14);
            EkDugme(153, y, 131, 34, Dil("Off"), !kilit, () => KilitSec(false), 14);
            y += 38;
            EkYazi(18, y, 264, 16, Dil("Nobody can pick it up, not even you."), 10, kilit ? EkKirmizi : EkNot, TextAnchor.MiddleLeft, false);
        }

        // alt kisim: sayfa degistirme + Reset / Done (iki sayfada da ayni yerde)
        private void SapMenuAlt()
        {
            float y = MH - 100;
            EkKutuCiz(16, y - 8, MW - 32, 2, EkKenar, false);
            EkDugme(16, y, 44, 36, "<", sapSayfa > 0, () => { sapSayfa = 0; }, 18);
            EkYazi(66, y, 168, 36, (sapSayfa + 1) + " / 2", 15, EkSoluk);
            EkDugme(240, y, 44, 36, ">", sapSayfa < 1, () => { sapSayfa = 1; }, 18);
            y += 46;
            EkDugme(16, y, 131, 38, Dil("Reset"), false, BoySifirla, 14);
            EkDugme(153, y, 131, 38, Dil("Done"), true, () => { sapMenuAcik = false; }, 14);
        }

        private void SapGirdi(Vector3 dPos)
        {
            bool fare = Application.isFocused && (GetAsyncKeyState(0x01) & 0x8000) != 0;
            bool fareTik = fare && !mFareOnceki && !menuAcik;
            mFareOnceki = fare;

            // dugme: VR parmak ucu dokununca / PC sol tik
            for (int k = 0; k < 4; k++)
            {
                Vector3 pp;
                if (!BasanNokta(k, out pp)) continue;
                float d = Vector3.Distance(pp, dPos);
                if (d > 0.055f) dHazir[k] = true;
                if (dBasili[k]) { if (d > 0.04f) dBasili[k] = false; }
                else if (dHazir[k] && d < 0.022f)
                {
                    dBasili[k] = true; dHazir[k] = false;
                    SapDugmeBas(k % 2);
                }
            }
            Camera cam = Camera.main;
            if (fareTik && cam != null)
            {
                Ray r = new Ray(cam.transform.position, cam.transform.forward);
                Vector3 fark = dPos - r.origin;
                float t = Vector3.Dot(fark, r.direction);
                if (t > 0f && t < 4f && (fark - r.direction * t).magnitude < 0.025f) { SapDugmeBas(-1); fareTik = false; }
            }

            // menu: iki yuz da basilabilir (on ve arka)
            if (!sapMenuAcik || sapMenu == null || !sapMenu.activeSelf) return;
            for (int yuz = 0; yuz < 2; yuz++)
            {
                Transform et = yuz == 0 ? sapMenu.transform : (sapMenuArka != null ? sapMenuArka.transform : null);
                if (et == null) continue;
                float olcek = Mathf.Abs(et.lossyScale.x);
                for (int k = 0; k < 4; k++)
                {
                    Vector3 pp;
                    if (!BasanNokta(k, out pp)) continue;
                    int bi = yuz * 4 + k;
                    Vector3 lp = et.InverseTransformPoint(pp);
                    float derin = lp.z * olcek;
                    bool icinde = Mathf.Abs(lp.x) < MW / 2f && Mathf.Abs(lp.y) < MH / 2f;
                    if (derin < -0.025f) mHazir[bi] = true;             // parmak bu yuzun onunde: basmaya hazir (arkadan gelen basmaz)
                    if (mBasili[bi]) { if (derin < -0.04f || !icinde) mBasili[bi] = false; }
                    else if (mHazir[bi] && icinde && derin > -0.012f && derin < 0.06f)
                    {
                        mBasili[bi] = true; mHazir[bi] = false;
                        if (SapMenuTikla(lp)) DugmeHissi(k % 2);
                    }
                    if (derin > 0.06f) mHazir[bi] = false;
                }
                if (fareTik && cam != null && Vector3.Dot(cam.transform.forward, et.forward) > 0f)   // sadece bakilan yuz
                {
                    Ray r = new Ray(cam.transform.position, cam.transform.forward);
                    Plane pl = new Plane(-et.forward, et.position);
                    float d;
                    if (pl.Raycast(r, out d) && d < 4f)
                    {
                        Vector3 lp = et.InverseTransformPoint(r.GetPoint(d));
                        if (Mathf.Abs(lp.x) < MW / 2f && Mathf.Abs(lp.y) < MH / 2f) { SapMenuTikla(lp); fareTik = false; }
                    }
                }
            }
        }

        private void SapDugmeBas(int h)
        {
            if (Time.time - dBasZaman < 0.3f) return;
            dBasZaman = Time.time;
            sapMenuAcik = !sapMenuAcik;
            if (h >= 0) DugmeHissi(h);
        }

        private bool SapMenuTikla(Vector3 lp)
        {
            if (Time.time - mTikZaman < 0.25f) return false;
            Vector2 p = new Vector2(lp.x + MW / 2f, MH / 2f - lp.y);
            for (int i = mTiklar.Count - 1; i >= 0; i--)
            {
                if (!mTiklar[i].Key.Contains(p)) continue;
                mTikZaman = Time.time;
                try { mTiklar[i].Value(); } catch (Exception e) { Logger.LogWarning("Pole menu click error: " + e.Message); }
                sapMenuImza = "";
                return true;
            }
            return false;
        }

        // =====================================================================
        // TIRMANMA: Utilla'nin modlu lobilerinde saplanmis bayraklar kati olur (sap + kumas collider'i):
        // el ve govde icinden gecmez, tirmanilabilir. Settings > CLIMB ile acilip kapanir.
        // =====================================================================
        private bool tirmanAcik = true;
        private BepInEx.Configuration.ConfigEntry<bool> tirmanCfg;
        private GameObject yerelTirman;
        private string yerelTirmanImza = "";
        private int yerelTirmanKatman = 0;
        private bool modluOnbellek; private float modluZaman = -10f;

        private void TirmanSec(bool a) { tirmanAcik = a; if (tirmanCfg != null) tirmanCfg.Value = a; }

        // bayrak hareketli bir seye (araba vb.) saplandi mi? (fizik govdesi var ya da baska modun arabasi)
        private bool sapliArac;
        private static bool AracMi(Collider c)
        {
            if (c == null) return false;
            if (c.attachedRigidbody != null) return true;
            for (Transform t = c.transform; t != null; t = t.parent)
            {
                string n = t.name;
                if (n.StartsWith("RagdollMod_Car") || n.IndexOf("Vehicle", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private bool ModluOda()
        {
            if (Time.time - modluZaman < 1f) return modluOnbellek;
            modluZaman = Time.time;
            modluOnbellek = false;
            try
            {
                var oda = Photon.Pun.PhotonNetwork.CurrentRoom;
                object o;
                if (oda != null && oda.CustomProperties != null && oda.CustomProperties.TryGetValue("gameMode", out o))
                {
                    string g = o as string;
                    modluOnbellek = g != null && g.IndexOf("MODDED", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception) { }
            return modluOnbellek;
        }

        // bayragin kok kemigine (model uzayi) sap kapsulu + kumas kutusu ekle
        private static GameObject TirmanKur(Transform kok, Mesh mesh, float donusDer, int katman)
        {
            if (kok == null || mesh == null) return null;
            Vector3[] v = mesh.vertices; Vector2[] uv = mesh.uv;
            float sx = 0f, sz = 0f; int sn = 0;
            float sAlt = float.MaxValue, sUst = float.MinValue;
            Vector3 kMin = new Vector3(float.MaxValue, float.MaxValue, 0f), kMax = new Vector3(float.MinValue, float.MinValue, 0f);
            for (int i = 0; i < v.Length && i < uv.Length; i++)
            {
                if (uv[i].x * 256f < 81f) { sx += v[i].x; sz += v[i].z; sn++; sAlt = Mathf.Min(sAlt, v[i].y); sUst = Mathf.Max(sUst, v[i].y); }
                else { kMin = Vector3.Min(kMin, v[i]); kMax = Vector3.Max(kMax, v[i]); }
            }
            if (sn == 0) return null;
            sx /= sn; sz /= sn;
            float s = Mathf.Max(0.01f, Mathf.Abs(kok.lossyScale.y));

            GameObject g = new GameObject("GorillaFlag_Climb");
            g.layer = katman;
            g.transform.SetParent(kok, false);
            CapsuleCollider cc = g.AddComponent<CapsuleCollider>();
            cc.direction = 1;
            cc.center = new Vector3(sx, (sAlt + sUst) * 0.5f, sz);
            float sapR = 0f;
            for (int i = 0; i < v.Length && i < uv.Length; i++)
                if (uv[i].x * 256f < 81f) sapR = Mathf.Max(sapR, new Vector2(v[i].x - sx, v[i].z - sz).magnitude);
            cc.radius = Mathf.Max(0.025f / s, sapR + 0.004f / s);
            cc.height = (sUst - sAlt) + cc.radius * 2f;

            // kumasa collider yok: el kumasin icinden gecer, sadece sapa tutunulur
            return g;
        }

        private void YerelTirmanKare()
        {
            bool goster = tirmanAcik && sapli && !yerde && !sapliArac && Time.time - sapliHareketZaman > 1f && tasiyan < 0 && yerelRgo != null && yerelKopya != null
                && yerelKopya.activeInHierarchy && ModluOda();
            if (!goster) { if (yerelTirman != null && yerelTirman.activeSelf) yerelTirman.SetActive(false); return; }
            string imza = BoyImza() + "|" + donus + "|" + yerelTirmanKatman;
            if (yerelTirman == null || yerelTirman.transform.parent != yerelRgo || imza != yerelTirmanImza)
            {
                if (yerelTirman != null) Destroy(yerelTirman);
                yerelTirman = TirmanKur(yerelRgo, yerelMesh, donus, yerelTirmanKatman);
                yerelTirmanImza = imza;
                if (yerelTirman == null) return;
            }
            if (!yerelTirman.activeSelf) yerelTirman.SetActive(true);
        }

        private void UzakTirmanKare()
        {
            bool modlu = tirmanAcik && ModluOda();
            foreach (var kv in uzaklar)
            {
                Uzak u = kv.Value;
                bool goster = modlu && u.durum == 3 && u != tasinan && u.rgo != null && u.kopya != null && u.kopya.activeInHierarchy && u.sapVar;
                if (!goster) { if (u.tirman != null && u.tirman.activeSelf) u.tirman.SetActive(false); u.tirmanYer = false; continue; }
                if (!u.tirmanYer)
                {
                    // zeminin katmanini al (oyuncunun tirmanabildigi katman)
                    Vector3 alt, ust; UzakSapUclari(u, out alt, out ust);
                    Vector3 eksen = (ust - alt).normalized;
                    RaycastHit h;
                    bool bulundu = ZeminBul(alt + eksen * 0.3f, -eksen, 1f, out h) && h.collider != null;
                    u.tirmanKatman = bulundu ? h.collider.gameObject.layer : 0;
                    u.tirmanArac = bulundu && AracMi(h.collider);
                    u.tirmanYer = true;
                }
                if (u.tirmanArac || Time.time - u.hareketZaman < 1f) { if (u.tirman != null && u.tirman.activeSelf) u.tirman.SetActive(false); continue; }   // arabadaki bayrak arabayi kilitlemesin
                string imza = u.ek + "|" + u.ol + "|" + u.kal + "|" + u.donus + "|" + u.tirmanKatman;
                if (u.tirman == null || u.tirman.transform.parent != u.rgo || imza != u.tirmanImza)
                {
                    if (u.tirman != null) Destroy(u.tirman);
                    u.tirman = TirmanKur(u.rgo, u.mesh, u.donus, u.tirmanKatman);
                    u.tirmanImza = imza;
                    if (u.tirman == null) continue;
                }
                if (!u.tirman.activeSelf) u.tirman.SetActive(true);
            }
        }


        // ---------- hareketli seylerin (araba, platform...) ustundeki bayrak onlarla birlikte gider ----------
        // Bayrak cocuk yapilmaz (olcekli objelerde bozulur): saplandigi objeye gore yeri saklanir, her karede oraya konur.
        private Transform sapliHedef;
        private Vector3 sapliLokPos, sapliHedefSon;
        private Quaternion sapliLokRot = Quaternion.identity;
        private float sapliHareketZaman = -10f;

        private void SapliHedefAyarla(Collider c)
        {
            sapliHedef = c != null ? c.transform : null;
            sapliArac = AracMi(c);
            if (sapliHedef == null || yerelRgo == null) return;
            sapliLokPos = sapliHedef.InverseTransformPoint(yerelRgo.position);
            sapliLokRot = Quaternion.Inverse(sapliHedef.rotation) * yerelRgo.rotation;
            sapliHedefSon = sapliHedef.position;
        }

        // bayragin altindaki obje (saplandigi / dustugu yer)
        private Collider AltindakiBul(Transform rgo, Vector3 alt, Vector3 ust, bool dik)
        {
            RaycastHit h;
            Vector3 eksen = (ust - alt).normalized;
            if (dik && eksen.sqrMagnitude > 0.5f) { if (ZeminBul(alt + eksen * 0.3f, -eksen, 0.6f, out h)) return h.collider; }
            else { Vector3 orta = (alt + ust) * 0.5f; if (ZeminBul(orta + Vector3.up * 0.3f, Vector3.down, 0.8f, out h)) return h.collider; }
            return null;
        }

        private void SapliTakip()
        {
            if (sapli && tasiyan < 0 && yerelRgo != null && sapliHedef != null && sapliHedef.gameObject.activeInHierarchy)
            {
                if ((sapliHedef.position - sapliHedefSon).sqrMagnitude > 1e-6f) sapliHareketZaman = Time.time;
                sapliHedefSon = sapliHedef.position;
                yerelRgo.SetPositionAndRotation(sapliHedef.TransformPoint(sapliLokPos), sapliHedef.rotation * sapliLokRot);
            }
            foreach (var kv in uzaklar)
            {
                Uzak u = kv.Value;
                if ((u.durum != 3 && u.durum != 4) || u.rgo == null || u.takipHedef == null || !u.takipHedef.gameObject.activeInHierarchy || u == tasinan) continue;
                if ((u.takipHedef.position - u.takipSon).sqrMagnitude > 1e-6f) u.hareketZaman = Time.time;
                u.takipSon = u.takipHedef.position;
                u.rgo.SetPositionAndRotation(u.takipHedef.TransformPoint(u.takipLokPos), u.takipHedef.rotation * u.takipLokRot);
            }
        }

        private void UzakTakipAyarla(Uzak u, bool dik)
        {
            u.takipHedef = null;
            if (u.rgo == null || !u.sapVar) return;
            Vector3 alt, ust; UzakSapUclari(u, out alt, out ust);
            Collider c = AltindakiBul(u.rgo, alt, ust, dik);
            if (c == null) return;
            u.takipHedef = c.transform;
            u.takipLokPos = c.transform.InverseTransformPoint(u.rgo.position);
            u.takipLokRot = Quaternion.Inverse(c.transform.rotation) * u.rgo.rotation;
            u.takipSon = c.transform.position;
            u.tirmanArac = AracMi(c);
            u.tirmanYer = true;
        }

        // ---------- firlatma: ucus fizigi (yercekimi, mizrak gibi ucu one doner, carpinca saplanir ya da yere duser) ----------
        // ragdoll gibi ucus: firlatan elin donusu + rastgele takla ile doner; kumasin hava direnci
        // (ust tarafta) ucu yavasca one cevirir ama kilitlemez, bayrak havada serbestce yalpalar
        private static Vector3 RastgeleTakla(Vector3 v)
        {
            Vector3 yon = v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
            Vector3 eksen = Vector3.Cross(yon, UnityEngine.Random.onUnitSphere);
            if (eksen.sqrMagnitude < 1e-4f) eksen = Vector3.Cross(yon, Vector3.up);
            if (eksen.sqrMagnitude < 1e-4f) eksen = Vector3.right;
            return eksen.normalized * UnityEngine.Random.Range(1.5f, 4f) * Mathf.Clamp(v.magnitude / 6f, 0.4f, 1.6f);
        }

        // elde tutulurken bayragin aci hizi (firlatinca donmeye devam etsin)
        private static Vector3 AciHiz(Quaternion onceki, Quaternion simdi, float dt)
        {
            Quaternion dq = simdi * Quaternion.Inverse(onceki);
            float ang; Vector3 ax;
            dq.ToAngleAxis(out ang, out ax);
            if (float.IsNaN(ax.x) || float.IsInfinity(ax.x) || ax.sqrMagnitude < 1e-8f) return Vector3.zero;
            if (ang > 180f) ang -= 360f;
            Vector3 w = ax.normalized * (ang * Mathf.Deg2Rad / Mathf.Max(dt, 0.001f));
            return w.magnitude > 30f ? Vector3.zero : w;
        }

        // carpinca seker (saplanmadiysa): hiz yansir, yavaslar, yeni takla
        private static bool Sek(Transform rgo, RaycastHit h, ref Vector3 v, ref Vector3 w, ref int sekme)
        {
            if (sekme >= 2 || v.magnitude < 3.5f || h.normal.sqrMagnitude < 0.5f) return false;
            sekme++;
            Vector3 n = h.normal.normalized;
            v = Vector3.Reflect(v, n) * 0.32f + n * 0.6f;
            w = w * 0.4f + RastgeleTakla(v) * 1.2f;
            rgo.position += n * 0.06f;
            return true;
        }

        private bool UcusAdim(Transform rgo, Vector3 aL, Vector3 uL, ref Vector3 v, ref Vector3 w, float dt, out RaycastHit hit, out bool uctan)
        {
            hit = new RaycastHit(); uctan = false;
            v += Physics.gravity * dt;
            v *= Mathf.Exp(-dt * 0.12f);                       // hafif hava direnci
            Vector3 alt0 = rgo.TransformPoint(aL), ust0 = rgo.TransformPoint(uL);
            Vector3 asagi = (alt0 - ust0).normalized;
            if (v.sqrMagnitude > 0.5f)
            {
                // kumas ustte oldugu icin ruzgar ucu one dogru ceviren bir tork yapar (rüzgar gulu); sadece hizlandirir, kilitlemez
                w += Vector3.Cross(asagi, v.normalized) * (Mathf.Min(v.magnitude, 15f) * 0.55f) * dt;
            }
            w *= Mathf.Exp(-dt * 0.7f);                        // donme yavas yavas soner
            float aci = w.magnitude * dt;
            if (aci > 1e-5f)
            {
                Quaternion d = Quaternion.AngleAxis(aci * Mathf.Rad2Deg, w.normalized);
                Vector3 orta = Vector3.Lerp(alt0, ust0, 0.6f);   // agirlik merkezi kumasa yakin
                rgo.rotation = d * rgo.rotation;
                rgo.position = orta + d * (rgo.position - orta);
            }
            Vector3 adim = v * dt;
            float uz = adim.magnitude;
            if (uz < 1e-5f) return false;
            Vector3 yon = adim / uz;
            Vector3 alt1 = rgo.TransformPoint(aL);
            if (ZeminBul(alt1 - yon * 0.05f, yon, uz + 0.08f, out hit))
            {
                Vector3 dn = (rgo.TransformPoint(aL) - rgo.TransformPoint(uL)).normalized;
                uctan = Vector3.Dot(v.normalized, dn) > 0.6f;
                rgo.position += yon * Mathf.Max(0f, hit.distance - 0.05f);
                return true;
            }
            Vector3 ust1 = rgo.TransformPoint(uL);
            if (ZeminBul(ust1, yon, uz + 0.05f, out hit)) { uctan = false; return true; }
            rgo.position += adim;
            return false;
        }

        private void KopyaSirtaBagla()
        {
            if (yerelKopya == null || yerelSlot == null || yerelRgo == null) return;
            Vector3 wp = yerelRgo.position; Quaternion wr = yerelRgo.rotation;
            yerelKopya.transform.SetParent(yerelSlot, false);
            yerelKopya.transform.localPosition = Vector3.zero;
            yerelKopya.transform.localRotation = Quaternion.identity;
            yerelKopya.transform.localScale = Vector3.one;
            yerelRgo.position = wp; yerelRgo.rotation = wr;
        }

        private void UcusKare()
        {
            if (devriliyor)
            {
                // yere degdi: tek karede yapismaz, degdigi yerden yumusakca devrilip yatar
                float k = (Time.time - devBasZaman) / DevrilmeSure;
                if (k >= 1f) { YereYatirBitir(); return; }
                k = k * k;   // dusme gibi hizlanir
                yerelRgo.position = Vector3.Lerp(devBasPos, devSonPos, k);
                yerelRgo.rotation = Quaternion.Slerp(devBasRot, devSonRot, k);
                return;
            }
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            Vector3 aL = new Vector3(yerelSapX, yerelSapAltY, yerelSapZ), uL = new Vector3(yerelSapX, yerelSapUstY, yerelSapZ);
            if (Time.time - ucusZaman > 6f || yerelRgo.position.y < -200f)
            {
                // bir yere degmedi: sirta yumusakca donsun
                ucuyor = false;
                KopyaSirtaBagla();
                donusBasPos = yerelRgo.position; donusBasRot = yerelRgo.rotation; donusBasZaman = Time.time; donusSuruyor = true;
                return;
            }
            RaycastHit h; bool uctan;
            if (!UcusAdim(yerelRgo, aL, uL, ref ucusHiz, ref ucusAci, dt, out h, out uctan)) return;
            float hiz = ucusHiz.magnitude;
            Vector3 asagi = (yerelRgo.TransformPoint(aL) - yerelRgo.TransformPoint(uL)).normalized;
            if (uctan && saplamaAcik && hiz >= FirlatSaplamaHizi) { ucuyor = false; Sapla(h, hiz, asagi); return; }
            if (Sek(yerelRgo, h, ref ucusHiz, ref ucusAci, ref ucusSekme)) { UcusAgKaydet(); return; }   // seker, ucmaya devam
            YereYatir(h);   // devrilme bitince ucuyor kapanir
        }

        private bool devriliyor;
        private float devBasZaman;
        private Vector3 devBasPos, devSonPos;
        private Quaternion devBasRot = Quaternion.identity, devSonRot = Quaternion.identity;
        private Collider devAltindaki;
        private const float DevrilmeSure = 0.4f;

        // diger oyuncular ucusu kendileri hesaplar: kalkis (ve her sekme) durumunu gonder
        private Vector3 ucusAci;
        private int ucusSekme, ucusNo;
        private string ucusAgPoz = "";
        private Quaternion tutRotOnceki = Quaternion.identity;
        private Vector3 tutAci;
        private void UcusAgKaydet()
        {
            if (yerelRgo == null) return;
            ucusNo++;
            ucusAgPoz = F7(yerelRgo.position, yerelRgo.rotation) + "," + F(yerelRgo.lossyScale.x) + ","
                + F(ucusHiz.x) + "," + F(ucusHiz.y) + "," + F(ucusHiz.z) + ","
                + F(ucusAci.x) + "," + F(ucusAci.y) + "," + F(ucusAci.z) + "," + ucusNo;
        }

        // ucan bayrakta kumas: hava akimi hizin tersine iter + cirpinma (donmus gibi durmasin)
        private Vector3 UcusKumasKuvvet(Vector3 v, float t)
        {
            Vector3 yan = Vector3.Cross(v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward, Vector3.up);
            if (yan.sqrMagnitude < 1e-4f) yan = Vector3.right;
            yan.Normalize();
            float cirp = (Mathf.PerlinNoise(t * 3.1f, 4.2f) - 0.5f) * 2f;
            float cirp2 = (Mathf.PerlinNoise(t * 2.3f, 8.7f) - 0.5f) * 2f;
            return Vector3.ClampMagnitude(-v * 1.3f, 22f) + yan * (cirp * (2.5f + v.magnitude * 0.35f)) + Vector3.up * (cirp2 * 2f);
        }

        // ucu one gelmediyse: bayrak carptigi yerin altindaki zemine yatar (birakilmis gibi, alinabilir)
        // yere degen bayrak: yatis pozunu hesapla, oraya yumusakca devrilsin (UcusKare)
        private void YereYatir(RaycastHit carp)
        {
            devBasPos = yerelRgo.position; devBasRot = yerelRgo.rotation;
            YatisPozu(carp, out devSonPos, out devSonRot, out devAltindaki);
            yerelRgo.position = devBasPos; yerelRgo.rotation = devBasRot;
            ucusHiz = Vector3.zero; ucusAci = Vector3.zero;
            devBasZaman = Time.time;
            devriliyor = true;
        }

        private void YereYatirBitir()
        {
            devriliyor = false;
            ucuyor = false;
            yerelRgo.position = devSonPos; yerelRgo.rotation = devSonRot;
            sapli = true; yerde = true; tasiyan = -1;
            sapliOda = OdaAdi();
            SapliHedefAyarla(devAltindaki);
            Logger.LogInfo("Local flag landed on the ground");
        }

        // yatis pozu: sap yere paralel, kumas yana (yerelRgo'yu bu poza tasir)
        private void YatisPozu(RaycastHit carp, out Vector3 pos, out Quaternion rot, out Collider altindaki)
        {
            Vector3 alt = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapAltY, yerelSapZ));
            Vector3 ust = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapUstY, yerelSapZ));
            Vector3 eksen = (ust - alt).normalized;
            Vector3 yatay = new Vector3(eksen.x, 0f, eksen.z);
            if (yatay.sqrMagnitude < 1e-4f) { yatay = yerelRgo.right; yatay.y = 0f; }
            if (yatay.sqrMagnitude < 1e-4f) yatay = Vector3.forward;
            yatay.Normalize();
            Quaternion son = Quaternion.FromToRotation(eksen, yatay) * yerelRgo.rotation;
            if (yerelKemikA != null && yerelKemikF != null)
            {
                Vector3 kum = son * (Quaternion.Inverse(yerelRgo.rotation) * (yerelKemikF.position - yerelKemikA.position));
                Vector3 dik = kum - yatay * Vector3.Dot(kum, yatay);
                Vector3 yan = Vector3.Cross(Vector3.up, yatay).normalized;
                if (dik.sqrMagnitude > 1e-6f) son = Quaternion.FromToRotation(dik.normalized, Vector3.Dot(dik, yan) >= 0f ? yan : -yan) * son;
            }
            yerelRgo.rotation = son;
            Vector3 orta = yerelRgo.TransformPoint(new Vector3(yerelSapX, (yerelSapAltY + yerelSapUstY) * 0.5f, yerelSapZ));
            RaycastHit z;
            altindaki = carp.collider;
            if (ZeminBul(orta + Vector3.up * 0.4f, Vector3.down, 30f, out z)) { yerelRgo.position += (z.point + Vector3.up * 0.025f) - orta; altindaki = z.collider; }
            else yerelRgo.position += (carp.point + Vector3.up * 0.025f) - orta;
            pos = yerelRgo.position; rot = yerelRgo.rotation;
        }

        // =====================================================================
        // GOREBOX (arkadasin silah modu, RagdollMod): yukluyse mermiler ve patlamalar bayraga hasar verir.
        // Saplanmis / yerde yatan bayragin 100 cani var. Vurulan yerde delik acilir (kumasla birlikte dalgalanir),
        // can yarinin altina inince sap mermi yonune egilir, can bitince bayrak parcalanir ve devrilir.
        // Can bayragin sahibinde hesaplanir ve gflag1 ile gider; delikleri herkes kendi gordugu mermilerden cizer.
        // Bayrak ele alininca ya da sirta donunce can dolar, delikler kapanir.
        // =====================================================================
        private static Plugin ben;
        private const float BayrakCanMax = 100f;
        private const float YikikDonusSure = 12f;          // parcalanan bayrak bu kadar yerde kalir, sonra sirta doner
        private const int DelikSiniri = 40;
        private bool goreBagli;
        private float goreDeneme;
        private int goreDenemeSay;
        private static object goreSilah;                    // o an ates eden silahin WeaponDef'i
        private static System.Reflection.MethodInfo goreHasarHesap, goreDefAl, goreKivilcim;
        private float bayrakCan = BayrakCanMax;
        private bool bayrakYikik;
        private float yikikZaman;
        private float egimToplam;
        private readonly List<GameObject> yerelDelikler = new List<GameObject>();
        private Mesh vurusMesh;
        private readonly List<Vector3> vurusV = new List<Vector3>();
        private readonly Dictionary<Mesh, int[]> vurusUcgen = new Dictionary<Mesh, int[]>();
        private Material delikMat;
        private Mesh delikQuad;

        private class Parca
        {
            public Transform t;
            public Material mat;
            public Vector3 v, w, olcek;
            public float bitis;
            public bool kumas, durdu;
        }
        private readonly List<Parca> parcalar = new List<Parca>();

        private void GoreKare()
        {
            GoreBagla();
            ParcaKare();
            // ele alininca / sirta donunce bayrak yenilenir
            if (!sapli && !ucuyor && (bayrakCan < BayrakCanMax || bayrakYikik || yerelDelikler.Count > 0)) BayrakOnar();
            if (bayrakYikik && sapli && yerde && tasiyan < 0 && Time.time - yikikZaman > YikikDonusSure)
            {
                SapliCoz();
                SirtaKoy();
                Logger.LogInfo("Destroyed flag returned to the back.");
            }
        }

        // Gorebox yuklendiyse ates / patlama fonksiyonlarina Harmony ile baglan (bir dakika boyunca dener)
        private void GoreBagla()
        {
            if (goreBagli || goreDenemeSay >= 12 || Time.time < goreDeneme) return;
            goreDeneme = Time.time + 5f;
            goreDenemeSay++;
            Type tMermi = HarmonyLib.AccessTools.TypeByName("RagdollMod.Ballistics");
            if (tMermi == null) return;
            goreBagli = true;
            try
            {
                Type tSilah = HarmonyLib.AccessTools.TypeByName("RagdollMod.WeaponManager");
                Type tDefs = HarmonyLib.AccessTools.TypeByName("RagdollMod.WeaponDefs");
                Type tPatla = HarmonyLib.AccessTools.TypeByName("RagdollMod.Explosions");
                Type tEfekt = HarmonyLib.AccessTools.TypeByName("RagdollMod.WeaponEffects");
                var h = new HarmonyLib.Harmony("gorillaflag.gorebox");
                h.Patch(HarmonyLib.AccessTools.Method(tMermi, "Fire"), postfix: GoreYama("GoreMermiSonra"));
                if (tSilah != null)
                {
                    h.Patch(HarmonyLib.AccessTools.Method(tSilah, "Fire"), prefix: GoreYama("GoreAtesOnce"), postfix: GoreYama("GoreAtesSonra"));
                    h.Patch(HarmonyLib.AccessTools.Method(tSilah, "OnRemoteFire"), prefix: GoreYama("GoreUzakAtesOnce"), postfix: GoreYama("GoreAtesSonra"));
                }
                if (tPatla != null) h.Patch(HarmonyLib.AccessTools.Method(tPatla, "Spawn"), postfix: GoreYama("GorePatlamaSonra"));
                if (tDefs != null)
                {
                    goreHasarHesap = HarmonyLib.AccessTools.Method(tDefs, "ComputeDamage");
                    goreDefAl = HarmonyLib.AccessTools.Method(tDefs, "Get");
                }
                if (tEfekt != null) goreKivilcim = HarmonyLib.AccessTools.Method(tEfekt, "SpawnImpact");
                Logger.LogInfo("Gorebox found: planted flags have " + BayrakCanMax + " HP against its weapons.");
            }
            catch (Exception e) { Logger.LogWarning("Gorebox hookup error: " + e.Message); }
        }

        private static HarmonyLib.HarmonyMethod GoreYama(string ad)
        {
            return new HarmonyLib.HarmonyMethod(typeof(Plugin).GetMethod(ad, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
        }

        private static void GoreAtesOnce(object[] __args) { goreSilah = __args != null && __args.Length > 0 ? __args[0] : null; }
        private static void GoreUzakAtesOnce(object[] __args)
        {
            goreSilah = null;
            try { if (goreDefAl != null && __args != null && __args.Length > 1) goreSilah = goreDefAl.Invoke(null, new object[] { __args[1] }); }
            catch (Exception) { }
        }
        private static void GoreAtesSonra() { goreSilah = null; }

        // her mermi (pompalida her saçma) icin: from = namlu, to = merminin durdugu yer
        private static void GoreMermiSonra(Vector3 from, Vector3 to)
        {
            if (ben == null) return;
            try { ben.MermiGeldi(from, to); }
            catch (Exception e) { ben.Logger.LogWarning("Flag bullet hit error: " + e.Message); }
        }

        private static void GorePatlamaSonra(Vector3 position, float radius)
        {
            if (ben == null) return;
            try { ben.PatlamaGeldi(position, radius); }
            catch (Exception e) { ben.Logger.LogWarning("Flag explosion hit error: " + e.Message); }
        }

        private SkinnedMeshRenderer YerelSmr()
        {
            return yerelRgo != null ? yerelRgo.GetComponentInChildren<SkinnedMeshRenderer>() : null;
        }

        // kendi bayragim sadece saplanmis / yerde yatarken vurulur (sirtta / elde mermi oyuncuya gelir)
        private bool YerelVurulabilir()
        {
            return yerelRgo != null && yerelKopya != null && yerelKopya.activeInHierarchy && sapli && tasiyan < 0 && !bayrakYikik;
        }

        private void MermiGeldi(Vector3 from, Vector3 to)
        {
            Vector3 nokta, normal;
            Vector3 yon = (to - from).normalized;
            if (YerelVurulabilir())
            {
                SkinnedMeshRenderer smr = YerelSmr();
                if (smr != null && BayrakVurus(smr, from, to, out nokta, out normal))
                {
                    DelikAc(smr, nokta, normal, yerelDelikler);
                    Kivilcim(nokta, normal);
                    BayrakHasar(MermiHasar(from, nokta), yon);
                }
            }
            foreach (Uzak u in uzaklar.Values)
            {
                if (u.smr == null || u.kopya == null || !u.kopya.activeInHierarchy || (u.durum != 3 && u.durum != 4) || u.can <= 0f) continue;
                if (BayrakVurus(u.smr, from, to, out nokta, out normal))
                {
                    DelikAc(u.smr, nokta, normal, u.delikler);
                    Kivilcim(nokta, normal);
                }
            }
        }

        private void PatlamaGeldi(Vector3 merkez, float yaricap)
        {
            if (!YerelVurulabilir()) return;
            SkinnedMeshRenderer smr = YerelSmr();
            if (smr == null) return;
            float menzil = Mathf.Max(yaricap, 0.5f) * 1.3f;
            Vector3 en = smr.bounds.ClosestPoint(merkez);
            float d = Vector3.Distance(en, merkez);
            if (d > menzil) return;
            float hasar = 150f * (1f - d / menzil);   // tam ortasinda tek patlama yeter
            Vector3 yon = en - merkez; yon.y = Mathf.Max(yon.y, 0f);
            for (int i = 0; i < 5 && hasar > 30f; i++)
            {
                // patlama kumasa birkac yanik delik acar
                Vector3 nokta, normal;
                Vector3 hedef = smr.bounds.center + Vector3.Scale(smr.bounds.extents, UnityEngine.Random.insideUnitSphere);
                if (BayrakVurus(smr, merkez, hedef + (hedef - merkez).normalized * 0.5f, out nokta, out normal)) DelikAc(smr, nokta, normal, yerelDelikler);
            }
            BayrakHasar(hasar, yon.sqrMagnitude > 1e-4f ? yon.normalized : Vector3.up);
        }

        private float MermiHasar(Vector3 from, Vector3 nokta)
        {
            if (goreSilah != null && goreHasarHesap != null)
            {
                try { return Convert.ToSingle(goreHasarHesap.Invoke(null, new object[] { goreSilah, 1, false, Vector3.Distance(from, nokta) })); }
                catch (Exception) { }
            }
            return 15f;
        }

        private void Kivilcim(Vector3 nokta, Vector3 normal)
        {
            if (goreKivilcim == null) return;
            try { goreKivilcim.Invoke(null, new object[] { nokta, normal }); } catch (Exception) { }
        }

        private void BayrakHasar(float hasar, Vector3 yon)
        {
            if (bayrakYikik || hasar <= 0f) return;
            bayrakCan = Mathf.Max(0f, bayrakCan - hasar);
            if (bayrakCan <= 0f) { BayrakParcala(yon); return; }
            // can yarinin altina inince sap mermi yonune egilir (en fazla 15 derece)
            if (yerde || bayrakCan >= BayrakCanMax * 0.5f || egimToplam >= 15f) return;
            Vector3 yatay = new Vector3(yon.x, 0f, yon.z);
            float aci = Mathf.Min(15f - egimToplam, hasar * 0.3f);
            if (yatay.sqrMagnitude < 1e-4f || aci <= 0f) return;
            Vector3 dip = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapAltY, yerelSapZ));
            Quaternion d = Quaternion.AngleAxis(aci, Vector3.Cross(Vector3.up, yatay.normalized));
            yerelRgo.rotation = d * yerelRgo.rotation;
            yerelRgo.position = dip + d * (yerelRgo.position - dip);
            egimToplam += aci;
            if (sapliHedef != null)
            {
                // arabaya / zemine bagli takip yeni aciyi korusun
                sapliLokPos = sapliHedef.InverseTransformPoint(yerelRgo.position);
                sapliLokRot = Quaternion.Inverse(sapliHedef.rotation) * yerelRgo.rotation;
            }
        }

        // can bitti: parcalar sacilir, bayrak saplandigi yerden kopup mermi yonunde devrilir
        private void BayrakParcala(Vector3 yon)
        {
            bayrakYikik = true;
            yikikZaman = Time.time;
            bayrakCan = 0f;
            ParcaSac(YerelSmr(), secili, yon);
            Logger.LogInfo("Local flag destroyed by weapon fire.");
            if (yerde) return;   // zaten yerde yatiyor
            Vector3 wp = yerelRgo.position; Quaternion wr = yerelRgo.rotation;
            SapliCoz();
            yerelRgo.position = wp; yerelRgo.rotation = wr;
            yerelKopya.transform.SetParent(null, true);
            Vector3 yatay = new Vector3(yon.x, 0f, yon.z);
            ucusHiz = (yatay.sqrMagnitude > 1e-4f ? yatay.normalized : Vector3.zero) * 1.8f + Vector3.up * 1.2f;
            ucusAci = RastgeleTakla(ucusHiz) * 0.6f;
            ucusSekme = 0;
            devriliyor = false;
            ucusZaman = Time.time;
            ucuyor = true;
            UcusAgKaydet();
        }

        private void BayrakOnar()
        {
            bayrakCan = BayrakCanMax;
            bayrakYikik = false;
            egimToplam = 0f;
            DelikTemizle(yerelDelikler);
        }

        private static void DelikTemizle(List<GameObject> liste)
        {
            foreach (GameObject g in liste) if (g != null) Destroy(g);
            liste.Clear();
        }

        // merminin cizgisi bayragin modelini (sap + kumas, o anki dalgali hali) kesiyor mu
        private bool BayrakVurus(SkinnedMeshRenderer smr, Vector3 a, Vector3 b, out Vector3 nokta, out Vector3 normal)
        {
            nokta = normal = Vector3.zero;
            Vector3 ab = b - a;
            float uz = ab.magnitude;
            if (uz < 1e-4f || smr.sharedMesh == null) return false;
            Bounds bo = smr.bounds;
            bo.Expand(0.05f);
            float giris;
            if (!bo.IntersectRay(new Ray(a, ab / uz), out giris) || giris > uz) return false;

            if (vurusMesh == null) vurusMesh = new Mesh();
            smr.BakeMesh(vurusMesh, true);
            vurusMesh.GetVertices(vurusV);
            int[] tr;
            if (!vurusUcgen.TryGetValue(smr.sharedMesh, out tr))
            {
                if (vurusUcgen.Count > 32) vurusUcgen.Clear();
                tr = smr.sharedMesh.triangles;
                vurusUcgen[smr.sharedMesh] = tr;
            }
            // BakeMesh(olcekli) koseleri renderer'in konum + donusune gore verir
            Matrix4x4 m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
            Matrix4x4 mi = m.inverse;
            Vector3 la = mi.MultiplyPoint3x4(a), ld = mi.MultiplyPoint3x4(b) - la;
            float enT = float.MaxValue; Vector3 enN = Vector3.zero;
            for (int i = 0; i + 2 < tr.Length; i += 3)
            {
                if (tr[i] >= vurusV.Count || tr[i + 1] >= vurusV.Count || tr[i + 2] >= vurusV.Count) continue;
                Vector3 v0 = vurusV[tr[i]], e1 = vurusV[tr[i + 1]] - v0, e2 = vurusV[tr[i + 2]] - v0;
                Vector3 pv = Vector3.Cross(ld, e2);
                float det = Vector3.Dot(e1, pv);
                if (Mathf.Abs(det) < 1e-9f) continue;   // iki yuzlu: kumasin iki tarafindan da vurulur
                float inv = 1f / det;
                Vector3 tv = la - v0;
                float u = Vector3.Dot(tv, pv) * inv;
                if (u < 0f || u > 1f) continue;
                Vector3 qv = Vector3.Cross(tv, e1);
                float v = Vector3.Dot(ld, qv) * inv;
                if (v < 0f || u + v > 1f) continue;
                float t = Vector3.Dot(e2, qv) * inv;
                if (t < 0f || t > 1f || t >= enT) continue;
                enT = t; enN = Vector3.Cross(e1, e2);
            }
            if (enT == float.MaxValue) return false;
            nokta = m.MultiplyPoint3x4(la + ld * enT);
            normal = m.MultiplyVector(enN).normalized;
            if (Vector3.Dot(normal, ab) > 0f) normal = -normal;   // mermiye bakan taraf
            return true;
        }

        // yanik mermi deligi: en yakin kemige baglanir, kumas dalgalaninca onunla gider; iki tarafta da gorunur
        private void DelikAc(SkinnedMeshRenderer smr, Vector3 nokta, Vector3 normal, List<GameObject> liste)
        {
            Material mat = DelikMat();
            if (mat == null || smr == null) return;
            Transform kemik = smr.rootBone; float enD = float.MaxValue;
            foreach (Transform k in smr.bones)
            {
                if (k == null) continue;
                float d = (k.position - nokta).sqrMagnitude;
                if (d < enD) { enD = d; kemik = k; }
            }
            if (kemik == null) return;
            liste.RemoveAll(x => x == null);
            while (liste.Count >= DelikSiniri) { Destroy(liste[0]); liste.RemoveAt(0); }

            GameObject g = new GameObject("GorillaFlag_Hole");
            g.layer = smr.gameObject.layer;
            g.transform.SetParent(kemik, false);
            g.transform.position = nokta;
            g.transform.rotation = Quaternion.LookRotation(normal) * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
            float boy = UnityEngine.Random.Range(0.028f, 0.045f);
            float o = Mathf.Max(Mathf.Abs(kemik.lossyScale.x), 1e-4f);
            g.transform.localScale = Vector3.one * (boy / o);
            for (int y = 0; y < 2; y++)
            {
                GameObject q = new GameObject("Side" + y);
                q.layer = g.layer;
                q.transform.SetParent(g.transform, false);
                q.transform.localPosition = new Vector3(0f, 0f, (y == 0 ? 0.002f : -0.002f) / boy);   // kumasin iki yuzune
                q.AddComponent<MeshFilter>().sharedMesh = DelikQuad();
                MeshRenderer r = q.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            liste.Add(g);
        }

        private Mesh DelikQuad()
        {
            if (delikQuad != null) return delikQuad;
            delikQuad = new Mesh { name = "GorillaFlag_HoleQuad" };
            delikQuad.vertices = new Vector3[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            delikQuad.uv = new Vector2[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            delikQuad.triangles = new int[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };   // iki yuzlu
            delikQuad.RecalculateBounds();
            return delikQuad;
        }

        // ortasi kopkoyu, kenari yanik kahverengi, disi saydam ve duzensiz
        private Material DelikMat()
        {
            if (delikMat != null) return delikMat;
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) return null;
            const int N = 64;
            Texture2D t = new Texture2D(N, N, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            Color32[] px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float aci = Mathf.Atan2(dy, dx);
                    float kenar = 0.78f + 0.14f * Mathf.Sin(aci * 5f) * Mathf.Sin(aci * 3f + 1.3f);   // yirtik kenar
                    float ic = 0.42f + 0.06f * Mathf.Sin(aci * 7f + 0.5f);
                    Color c;
                    if (r < ic) c = new Color(0.03f, 0.025f, 0.02f, 1f);
                    else if (r < kenar)
                    {
                        float k = (r - ic) / (kenar - ic);
                        c = Color.Lerp(new Color(0.12f, 0.07f, 0.03f, 0.95f), new Color(0.3f, 0.2f, 0.1f, 0f), k * k);
                    }
                    else c = new Color(0f, 0f, 0f, 0f);
                    px[y * N + x] = c;
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            delikMat = new Material(sh) { name = "GorillaFlag_HoleMat", mainTexture = t };
            if (delikMat.HasProperty("_BaseMap")) delikMat.SetTexture("_BaseMap", t);
            delikMat.renderQueue = 3000;
            return delikMat;
        }

        // parcalanma: bayragin renklerinde kumas parcalari + sap kiymiklari havaya sacilir, yere duser, kaybolur
        private void ParcaSac(SkinnedMeshRenderer smr, int idx, Vector3 yon)
        {
            if (smr == null) return;
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) return;
            Bounds b = smr.bounds;
            Texture2D tex = idx >= 0 && idx < bayraklar.Count ? bayraklar[idx] : null;
            Vector3 yatay = new Vector3(yon.x, 0f, yon.z);
            yatay = yatay.sqrMagnitude > 1e-4f ? yatay.normalized : Vector3.zero;
            for (int i = 0; i < 30; i++)
            {
                bool kumas = i >= 6;
                GameObject g = GameObject.CreatePrimitive(kumas ? PrimitiveType.Quad : PrimitiveType.Cube);
                Collider col = g.GetComponent<Collider>();
                if (col != null) Destroy(col);
                g.name = "GorillaFlag_Piece";
                g.layer = smr.gameObject.layer;
                Material m = new Material(sh);
                Color c = kumas ? ParcaRenk(tex) : new Color(0.38f, 0.31f, 0.24f, 1f);
                m.color = c;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
                Renderer r = g.GetComponent<Renderer>();
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                Vector3 olcek = kumas
                    ? new Vector3(UnityEngine.Random.Range(0.04f, 0.1f), UnityEngine.Random.Range(0.03f, 0.08f), 1f)
                    : new Vector3(0.018f, UnityEngine.Random.Range(0.05f, 0.12f), 0.018f);
                g.transform.position = b.center + Vector3.Scale(b.extents, new Vector3(UnityEngine.Random.Range(-0.9f, 0.9f), UnityEngine.Random.Range(-0.9f, 0.9f), UnityEngine.Random.Range(-0.9f, 0.9f)));
                g.transform.rotation = UnityEngine.Random.rotation;
                g.transform.localScale = olcek;
                parcalar.Add(new Parca
                {
                    t = g.transform, mat = m, olcek = olcek, kumas = kumas,
                    v = UnityEngine.Random.insideUnitSphere * 2.2f + Vector3.up * UnityEngine.Random.Range(1.5f, 3f) + yatay * 1.5f,
                    w = UnityEngine.Random.insideUnitSphere * 600f,
                    bitis = Time.time + UnityEngine.Random.Range(4f, 6.5f)
                });
            }
        }

        private static Color ParcaRenk(Texture2D tex)
        {
            try
            {
                if (tex != null && tex.isReadable)
                {
                    Color c = tex.GetPixelBilinear(UnityEngine.Random.value, UnityEngine.Random.value);
                    c.a = 1f;
                    return c;
                }
            }
            catch (Exception) { }
            return new Color(0.85f, 0.85f, 0.85f, 1f);
        }

        private void ParcaKare()
        {
            if (parcalar.Count == 0) return;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            for (int i = parcalar.Count - 1; i >= 0; i--)
            {
                Parca p = parcalar[i];
                if (p.t == null || Time.time > p.bitis)
                {
                    if (p.t != null) Destroy(p.t.gameObject);
                    if (p.mat != null) Destroy(p.mat);
                    parcalar.RemoveAt(i);
                    continue;
                }
                if (!p.durdu)
                {
                    // kumas parcasi havada suzulerek iner, kiymik duz duser
                    p.v += Physics.gravity * (p.kumas ? 0.35f : 1f) * dt;
                    if (p.kumas) p.v *= Mathf.Exp(-dt * 2.2f);
                    Vector3 adim = p.v * dt;
                    RaycastHit h;
                    if (adim.sqrMagnitude > 1e-8f && ZeminBul(p.t.position, adim.normalized, adim.magnitude + 0.01f, out h))
                    {
                        p.t.position = h.point + h.normal * 0.004f;
                        if (p.kumas) p.t.rotation = Quaternion.LookRotation(-h.normal) * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
                        p.durdu = true;
                    }
                    else
                    {
                        p.t.position += adim;
                        p.t.rotation = Quaternion.Euler(p.w * dt) * p.t.rotation;
                    }
                }
                float kalan = p.bitis - Time.time;
                if (kalan < 1f) p.t.localScale = p.olcek * Mathf.Max(0.01f, kalan);   // son saniyede kuculup kaybolur
            }
        }
        // =====================================================================
        // PAYLASIM: ozel odada, modu olan baska oyuncular sapladigim bayragi alip tasiyabilir,
        // birakinca yere duser, yeniden saplayabilir. Settings'ten acilip kapanir.
        // Tasiyan oyuncu kendi Photon ozelliginde "gflag2" yazar: hedefAktor|sira|mod|poz
        //   mod 1/2: sol/sag elinde (poz = el kemigine gore), 3: sapladi, 4: birakti (poz = dunya)
        // Bayragin sahibi bunu gorur, kendi bayragina uygular ve "ack" ile onaylar.
        // =====================================================================
        private const string AgTasimaAnahtar = "gflag2";
        private bool alinabilir = true;
        private BepInEx.Configuration.ConfigEntry<bool> alinabilirCfg;
        private float donusMesafe = 60f;               // 0 = hic donmez
        private BepInEx.Configuration.ConfigEntry<float> donusMesafeCfg;
        private static readonly float[] DonusMesafeleri = { 30f, 60f, 120f, 250f, 0f };
        private static readonly string[] DonusMesafeAd = { "30 m", "60 m", "120 m", "250 m", "Never" };

        // sahip tarafi (benim bayragim)
        private bool yerde;                    // birakilmis: yere yatik, ruzgar yok
        private int tasiyan = -1;              // bayragimi elinde tasiyan oyuncu
        private int tasiyanH;
        private Vector3 tasiyanPos; private Quaternion tasiyanRot = Quaternion.identity;
        private string tasAck = "";
        private readonly Dictionary<int, int> tasSonSira = new Dictionary<int, int>();

        // tasiyan tarafi (baskasinin bayragi elimde)
        private Uzak tasinan;
        private int tasinanH;
        private Vector3 tasinanPos; private Quaternion tasinanRot = Quaternion.identity;
        private int tasSira = 0;
        private string tasGonderilen;
        private Uzak tasBekleyen;              // birakip/sapladiktan sonra sahibin onayini bekleyen
        private float tasBekleZaman;
        private Vector3 tasBasPos, tasSonPos; private Quaternion tasBasRot, tasSonRot = Quaternion.identity;
        private readonly bool[] tasOncekiGrab = new bool[2];
        private Vector3 tasOncekiUc; private bool tasOncekiUcVar;
        private bool tasUcuyor, tasHizVar;
        private Vector3 tasUcusAci, tasAci;
        private Quaternion tasRotOnceki = Quaternion.identity;
        private int tasSekme;
        private Vector3 tasUcusHiz, tasHiz, tasOnceki;
        private float tasUcusZaman;

        private void AlinabilirSec(bool a) { alinabilir = a; if (alinabilirCfg != null) alinabilirCfg.Value = a; }
        private void DonusMesafeSec(float m) { donusMesafe = m; if (donusMesafeCfg != null) donusMesafeCfg.Value = m; }

        private static int BenimAktor()
        {
            try { return Photon.Pun.PhotonNetwork.LocalPlayer != null ? Photon.Pun.PhotonNetwork.LocalPlayer.ActorNumber : -1; }
            catch (Exception) { return -1; }
        }

        private void TasGonder(string v)
        {
            if (v == tasGonderilen) return;
            try
            {
                ExitGames.Client.Photon.Hashtable h = new ExitGames.Client.Photon.Hashtable();
                h[AgTasimaAnahtar] = v;
                if (Photon.Pun.PhotonNetwork.LocalPlayer != null) Photon.Pun.PhotonNetwork.LocalPlayer.SetCustomProperties(h);
                tasGonderilen = v;
            }
            catch (Exception e) { Logger.LogWarning("Flag carry sync error: " + e.Message); }
        }

        // uzak bayragin sap ucu ve tepesi (mesh'ten bir kez hesaplanir)
        private void UzakSapHesapla(Uzak u)
        {
            u.sapVar = false;
            if (u.mesh == null) return;
            Vector3[] v = u.mesh.vertices; Vector2[] uv = u.mesh.uv;
            float sx = 0f, sz = 0f, alt = float.MaxValue, ust = float.MinValue; int n = 0;
            for (int i = 0; i < v.Length && i < uv.Length; i++)
                if (uv[i].x * 256f < 81f) { sx += v[i].x; sz += v[i].z; n++; alt = Mathf.Min(alt, v[i].y); ust = Mathf.Max(ust, v[i].y); }
            if (n == 0) return;
            u.sapX = sx / n; u.sapZ = sz / n; u.sapAlt = alt; u.sapUst = ust; u.sapVar = true;
        }

        private void UzakSapUclari(Uzak u, out Vector3 alt, out Vector3 ust)
        {
            alt = u.rgo.TransformPoint(new Vector3(u.sapX, u.sapAlt, u.sapZ));
            ust = u.rgo.TransformPoint(new Vector3(u.sapX, u.sapUst, u.sapZ));
        }

        // ---------- sahip: diger oyuncularin tasima mesajlarini benim bayragima uygula ----------
        private void TasimaDinle(bool ozel)
        {
            if (!ozel)
            {
                if (tasiyan >= 0) { tasiyan = -1; SapliCoz(); SirtaKoy(); }
                tasSonSira.Clear();
                return;
            }
            int ben = BenimAktor();
            bool tasiyanVar = false;
            foreach (Photon.Realtime.Player p in Photon.Pun.PhotonNetwork.PlayerListOthers)
            {
                if (p == null) continue;
                if (p.ActorNumber == tasiyan) tasiyanVar = true;
                if (p.CustomProperties == null) continue;
                object o;
                if (!p.CustomProperties.TryGetValue(AgTasimaAnahtar, out o)) continue;
                string v = o as string;
                if (string.IsNullOrEmpty(v)) continue;
                string[] q = v.Split('|');
                if (q.Length < 4) continue;
                int hedef, sira, mod;
                if (!int.TryParse(q[0], out hedef) || hedef != ben) continue;
                if (!int.TryParse(q[1], out sira) || !int.TryParse(q[2], out mod)) continue;
                int son;
                if (tasSonSira.TryGetValue(p.ActorNumber, out son) && son >= sira) continue;
                tasSonSira[p.ActorNumber] = sira;
                if (!gizli || yerelKopya == null || yerelRgo == null) continue;
                float[] poz = Sayilar(q[3]);
                if (poz.Length < 7) continue;
                Vector3 pp = new Vector3(poz[0], poz[1], poz[2]);
                Quaternion pr = new Quaternion(poz[3], poz[4], poz[5], poz[6]);
                bool serbest = sapli && tasiyan < 0 && !tutuluyor;
                bool onda = tasiyan == p.ActorNumber;
                if (mod == 1 || mod == 2)
                {
                    if (!(alinabilir && !kilit && serbest)) continue;
                    tasiyan = p.ActorNumber; tasiyanH = mod - 1; tasiyanPos = pp; tasiyanRot = pr;
                    yerde = false;
                    tasiyanVar = true;
                    tasAck = p.ActorNumber + ":" + sira;
                    Logger.LogInfo("Local flag taken by player " + p.ActorNumber);
                }
                else if (mod == 3 || mod == 4)
                {
                    if (!(onda || (alinabilir && !kilit && serbest))) continue;
                    tasiyan = -1;
                    yerelKopya.transform.SetParent(null, true); sapli = true; sapliArac = false;
                    sapliOda = OdaAdi();
                    yerelRgo.position = pp; yerelRgo.rotation = pr;
                    {
                        Vector3 a0 = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapAltY, yerelSapZ));
                        Vector3 a1 = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapUstY, yerelSapZ));
                        if (mod == 3 && dikAcik) { sapliHedef = null; DikDuzelt(); a0 = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapAltY, yerelSapZ)); a1 = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapUstY, yerelSapZ)); }
                        SapliHedefAyarla(AltindakiBul(yerelRgo, a0, a1, mod == 3));
                    }
                    yerde = mod == 4;
                    if (yerelKemikA != null && yerelKemikF != null)
                    {
                        Vector3 kd = yerelKemikF.position - yerelKemikA.position; kd.y = 0f;
                        ruzgarYon = kd.sqrMagnitude > 1e-6f ? kd.normalized : Vector3.forward;
                    }
                    tasAck = p.ActorNumber + ":" + sira;
                    Logger.LogInfo("Local flag " + (mod == 3 ? "planted" : "dropped") + " by player " + p.ActorNumber);
                }
            }
            // tasiyan odadan cikti: bayrak sirta doner
            if (tasiyan >= 0 && !tasiyanVar) { tasiyan = -1; SapliCoz(); SirtaKoy(); Logger.LogInfo("Local flag carrier left, flag returned."); }
        }

        // sahip: tasinan bayragimi tasiyanin elinde goster (her kare)
        // oyuncunun gorillasini her karede aramak cok agir (FindObjectsOfType): onbellekten al
        private readonly Dictionary<int, Component> rigOnbellek = new Dictionary<int, Component>();
        private readonly Dictionary<int, float> rigAramaZaman = new Dictionary<int, float>();
        private Component RigOnbellekli(int aktor)
        {
            Component rig;
            Uzak u;
            if (uzaklar.TryGetValue(aktor, out u) && u.rig != null) return u.rig;
            if (rigOnbellek.TryGetValue(aktor, out rig) && rig != null) return rig;
            float z;
            if (rigAramaZaman.TryGetValue(aktor, out z) && Time.time - z < 2f) return null;
            rigAramaZaman[aktor] = Time.time;
            rig = RigBul(aktor);
            rigOnbellek[aktor] = rig;
            return rig;
        }

        private void TasiyanKare()
        {
            if (tasiyan < 0 || yerelRgo == null) return;
            Component rig = RigOnbellekli(tasiyan);
            Transform el = RigEl(rig, tasiyanH);
            if (el == null) return;
            yerelRgo.position = el.TransformPoint(tasiyanPos);
            yerelRgo.rotation = el.rotation * tasiyanRot;
        }

        // ---------- tasiyan: baskasinin bayragini tut / tasi / birak / sapla ----------
        private void TasimaKare()
        {
            bool ozel = OzelOdada();
            if (!ozel)
            {
                if (tasinan != null || tasBekleyen != null) { tasinan = null; tasBekleyen = null; }
                if (tasGonderilen != null) TasGonder(null);
                return;
            }
            if (tasinan != null && (tasinan.kopya == null || tasinan.rgo == null || !uzaklar.ContainsValue(tasinan))) { tasinan = null; TasGonder(null); }
            if (tasinan == null) tasUcuyor = false;

            for (int h = 0; h < 2; h++)
            {
                Transform el = El(h);
                bool g = el != null && Tutuyor(h);
                if (g && !tasOncekiGrab[h] && tasinan == null && !tutuluyor)
                {
                    Uzak en = null; float enD = 0.3f;
                    foreach (Uzak u in uzaklar.Values)
                    {
                        if (u == tasBekleyen || !u.izin || u.kopya == null || u.rgo == null || !u.kopya.activeInHierarchy) continue;
                        if (u.durum != 3 && u.durum != 4) continue;
                        if (!u.sapVar) UzakSapHesapla(u);
                        if (!u.sapVar) continue;
                        Vector3 a, b; UzakSapUclari(u, out a, out b);
                        float d = NoktaDogru(el.position, a, b);
                        if (d < enD) { enD = d; en = u; }
                    }
                    if (en != null) TasimaBasla(en, h, el);
                }
                if (!g && tasOncekiGrab[h] && tasinan != null && tasinanH == h && !tasUcuyor)
                {
                    // birakinca duz yere yapismaz: elin hiziyla ucar / duser, degdigi yerde saplanir ya da yatar
                    tasUcuyor = true;
                    tasUcusHiz = firlatAcik && tasHizVar ? tasHiz * (tasHiz.magnitude > FirlatEsik ? FirlatCarpan : 1f) : Vector3.zero;
                    tasUcusAci = firlatAcik && tasHizVar ? tasAci + RastgeleTakla(tasUcusHiz) : Vector3.zero;
                    tasSekme = 0;
                    tasUcusZaman = Time.time;
                }
                tasOncekiGrab[h] = g;
            }

            if (tasinan != null && tasUcuyor)
            {
                Uzak tu = tasinan;
                float dt = Mathf.Min(Time.deltaTime, 0.05f);
                Vector3 aL = new Vector3(tu.sapX, tu.sapAlt, tu.sapZ), uL = new Vector3(tu.sapX, tu.sapUst, tu.sapZ);
                RaycastHit th; bool uctan;
                bool degdi = UcusAdim(tu.rgo, aL, uL, ref tasUcusHiz, ref tasUcusAci, dt, out th, out uctan);
                if (tu.boing != null) BoingYercekimiAyar(tu.boing, UcusKumasKuvvet(tasUcusHiz, Time.time + tu.tohum));
                float hz0 = tasUcusHiz.magnitude;
                bool saplar = degdi && uctan && saplamaAcik && hz0 >= FirlatSaplamaHizi;
                if (degdi && !saplar && Time.time - tasUcusZaman <= 6f && Sek(tu.rgo, th, ref tasUcusHiz, ref tasUcusAci, ref tasSekme)) degdi = false;
                if (degdi || Time.time - tasUcusZaman > 6f)
                {
                    tasUcuyor = false;
                    if (tu.boing != null) BoingYercekimiAyar(tu.boing, Vector3.zero);
                    Vector3 asg = (tu.rgo.TransformPoint(aL) - tu.rgo.TransformPoint(uL)).normalized;
                    if (saplar) TasimaSapla(th, hz0, asg);
                    else TasimaBirak();   // carptigi yerin altindaki zemine yatar
                }
                return;
            }
            if (tasinan != null)
            {
                Transform kemik = RigEl(YerelRig(), tasinanH);
                if (kemik == null) kemik = El(tasinanH);
                if (kemik == null) return;
                if (tasinan.kopya.transform.parent != null) tasinan.kopya.transform.SetParent(null, true);
                tasinan.rgo.position = kemik.TransformPoint(tasinanPos);
                tasinan.rgo.rotation = kemik.rotation * tasinanRot;
                {
                    float dth = Mathf.Max(Time.deltaTime, 0.001f);
                    Vector3 pc = tasinan.rgo.position;
                    Vector3 anlik = tasHizVar ? (pc - tasOnceki) / dth : Vector3.zero;
                    if (anlik.magnitude > 40f) anlik = Vector3.zero;
                    tasHiz = tasHizVar ? Vector3.Lerp(tasHiz, anlik, 0.5f) : anlik;
                    Vector3 wAn = tasHizVar ? AciHiz(tasRotOnceki, tasinan.rgo.rotation, dth) : Vector3.zero;
                    tasAci = tasHizVar ? Vector3.Lerp(tasAci, wAn, 0.5f) : Vector3.zero;
                    tasRotOnceki = tasinan.rgo.rotation;
                    tasOnceki = pc; tasHizVar = true;
                }

                // yere saplama: kendi bayragimdaki gibi
                Vector3 alt, ust; UzakSapUclari(tasinan, out alt, out ust);
                Vector3 asagi = (alt - ust).normalized;
                float dtu = Mathf.Max(Time.deltaTime, 0.001f);
                float hizUc = tasOncekiUcVar ? Vector3.Dot((alt - tasOncekiUc) / dtu, asagi) : 0f;
                tasOncekiUc = alt; tasOncekiUcVar = true;
                if (hizUc > 30f) hizUc = 0f;
                if (saplamaAcik && hizUc >= SaplamaHizi && XBasili())
                {
                    Vector3 bas = Vector3.Lerp(alt, ust, 0.3f);
                    float uz = Vector3.Distance(bas, alt) + 0.1f;
                    RaycastHit zemin;
                    if (ZeminBul(bas, asagi, uz, out zemin)) TasimaSapla(zemin, hizUc, asagi);
                }
                return;
            }
            tasOncekiUcVar = false;

            if (tasBekleyen != null)
            {
                Uzak u = tasBekleyen;
                bool onay = u.tasAck == BenimAktor() + ":" + tasSira;
                if (onay || Time.time - tasBekleZaman > 4f || u.rgo == null)
                {
                    tasBekleyen = null;
                    u.degisti = true;          // sahibin gonderdigi duruma don
                    TasGonder(null);
                }
                else
                {
                    // dusme animasyonu (0.25 sn)
                    float k = Mathf.Clamp01((Time.time - tasBekleZaman) / 0.25f);
                    k = k * k;
                    if (u.kopya.transform.parent != null) u.kopya.transform.SetParent(null, true);
                    u.rgo.position = Vector3.Lerp(tasBasPos, tasSonPos, k);
                    u.rgo.rotation = Quaternion.Slerp(tasBasRot, tasSonRot, k);
                }
            }
        }

        private void TasimaBasla(Uzak u, int h, Transform el)
        {
            tasUcuyor = false; tasHizVar = false;
            Transform kemik = RigEl(YerelRig(), h);
            if (kemik != null && tutKVar[h])
            {
                tasinanPos = tutKPos[h]; tasinanRot = tutKRot[h];
            }
            else
            {
                Transform ref_ = kemik != null ? kemik : el;
                Vector3 a, b; UzakSapUclari(u, out a, out b);
                Vector3 tutNokta = Vector3.Lerp(a, b, 0.12f);
                Vector3 yeniPos = u.rgo.position + (el.position - tutNokta);
                tasinanPos = ref_.InverseTransformPoint(yeniPos);
                tasinanRot = Quaternion.Inverse(ref_.rotation) * u.rgo.rotation;
            }
            tasinan = u; tasinanH = h; tasBekleyen = null;
            tasOncekiUcVar = false;
            tasSira++;
            TasGonder(u.aktor + "|" + tasSira + "|" + (h + 1) + "|" + F7(tasinanPos, tasinanRot));
            Logger.LogInfo("Picked up the flag of player " + u.aktor);
        }

        private void TasimaBirak()
        {
            Uzak u = tasinan;
            tasinan = null;
            if (u == null || u.rgo == null) return;
            // yere dus: sap yatay, kumas yatay, zemine yat
            Vector3 alt, ust; UzakSapUclari(u, out alt, out ust);
            Vector3 eksen = (ust - alt).normalized;
            Vector3 yatay = new Vector3(eksen.x, 0f, eksen.z);
            if (yatay.sqrMagnitude < 1e-4f) { yatay = u.rgo.right; yatay.y = 0f; }
            if (yatay.sqrMagnitude < 1e-4f) yatay = Vector3.forward;
            yatay.Normalize();
            Quaternion r1 = Quaternion.FromToRotation(eksen, yatay) * u.rgo.rotation;
            Quaternion son = r1;
            if (u.kA != null && u.kF != null)
            {
                Vector3 kum = r1 * (Quaternion.Inverse(u.rgo.rotation) * (u.kF.position - u.kA.position));
                Vector3 dik = kum - yatay * Vector3.Dot(kum, yatay);
                Vector3 yan = Vector3.Cross(Vector3.up, yatay).normalized;
                if (dik.sqrMagnitude > 1e-6f)
                {
                    Vector3 hedefYon = Vector3.Dot(dik, yan) >= 0f ? yan : -yan;
                    son = Quaternion.FromToRotation(dik.normalized, hedefYon) * r1;
                }
            }
            Vector3 basPos = u.rgo.position; Quaternion basRot = u.rgo.rotation;
            u.rgo.rotation = son;
            Vector3 ortaYerel = new Vector3(u.sapX, (u.sapAlt + u.sapUst) * 0.5f, u.sapZ);
            Vector3 orta = u.rgo.TransformPoint(ortaYerel);
            RaycastHit zemin;
            if (ZeminBul(orta + Vector3.up * 0.3f, Vector3.down, 60f, out zemin))
                u.rgo.position += (zemin.point + Vector3.up * 0.025f) - orta;
            tasSonPos = u.rgo.position; tasSonRot = u.rgo.rotation;
            tasBasPos = basPos; tasBasRot = basRot;
            u.rgo.position = basPos; u.rgo.rotation = basRot;
            TasimaBitir(u, 4);
        }

        private void TasimaSapla(RaycastHit h, float hiz, Vector3 asagi)
        {
            Uzak u = tasinan;
            tasinan = null;
            if (u == null || u.rgo == null) return;
            Vector3 yukari = -asagi;
            float derinlik = Mathf.Clamp(0.03f + (hiz - SaplamaHizi) * 0.03f, 0.03f, 0.15f);
            Vector3 alt, ust; UzakSapUclari(u, out alt, out ust);
            Vector3 hedef = h.point - yukari * derinlik;
            u.rgo.position += hedef - alt;
            tasSonPos = u.rgo.position; tasSonRot = u.rgo.rotation;
            tasBasPos = tasSonPos; tasBasRot = tasSonRot;
            TasimaBitir(u, 3);
        }

        private void TasimaBitir(Uzak u, int mod)
        {
            tasSira++;
            tasBekleyen = u;
            tasBekleZaman = Time.time;
            TasGonder(u.aktor + "|" + tasSira + "|" + mod + "|" + F7(tasSonPos, tasSonRot));
            Logger.LogInfo((mod == 3 ? "Planted" : "Dropped") + " the flag of player " + u.aktor);
        }

        // baskasinin elinde tasinan uzak bayrak (3. kisi icin): tasiyanin gflag2'sinden el pozu
        private bool UzakTasinanYerlestir(Uzak u, int tasiyanAktor)
        {
            try
            {
                Photon.Realtime.Player p = Photon.Pun.PhotonNetwork.CurrentRoom != null ? Photon.Pun.PhotonNetwork.CurrentRoom.GetPlayer(tasiyanAktor) : null;
                if (p == null || p.CustomProperties == null) return false;
                object o;
                if (!p.CustomProperties.TryGetValue(AgTasimaAnahtar, out o)) return false;
                string[] q = (o as string ?? "").Split('|');
                int hedef, mod;
                if (q.Length < 4 || !int.TryParse(q[0], out hedef) || hedef != u.aktor || !int.TryParse(q[2], out mod) || (mod != 1 && mod != 2)) return false;
                float[] poz = Sayilar(q[3]);
                if (poz.Length < 7) return false;
                Transform el = RigEl(RigOnbellekli(tasiyanAktor), mod - 1);
                if (el == null) return false;
                Vector3 ws = u.rgo.lossyScale;
                if (u.kopya.transform.parent != el)
                {
                    u.kopya.transform.SetParent(el, false);
                    u.kopya.transform.localPosition = Vector3.zero;
                    u.kopya.transform.localRotation = Quaternion.identity;
                    u.kopya.transform.localScale = Vector3.one;
                }
                u.rgo.position = el.TransformPoint(new Vector3(poz[0], poz[1], poz[2]));
                u.rgo.rotation = el.rotation * new Quaternion(poz[3], poz[4], poz[5], poz[6]);
                Vector3 es = el.lossyScale;
                u.rgo.localScale = new Vector3(ws.x / Mathf.Max(es.x, 1e-4f), ws.y / Mathf.Max(es.y, 1e-4f), ws.z / Mathf.Max(es.z, 1e-4f));
                u.kopya.SetActive(true);
                return true;
            }
            catch (Exception) { return false; }
        }

        // =====================================================================
        // Ozel odada: Private bayragim modu olan diger oyunculara da gorunsun (ve onlarinki bana).
        // Sadece GIZLI (ozel) odalarda. Herkese acik lobide hicbir veri gonderilmez, gelen de cizilmez.
        // Veri, oyuncunun Photon ozelliklerinde tek bir kisa yazi olarak durur.
        // =====================================================================
        private const string AgAnahtar = "gflag1";
        private string agSon;            // en son gonderilen deger (null = yok)
        private float agZaman;
        private Type tVRRig;

        private class Uzak
        {
            public int aktor;
            public string veri = "";
            public bool degisti = true;
            public Component rig;
            public float rigZaman = -10f;
            public Transform slot;
            public GameObject kopya;
            public Transform rgo;
            public Component boing;
            public Transform kA, kF;
            public SkinnedMeshRenderer smr;
            public Mesh mesh;
            public Kayit kayit;
            public string dokuKod = "";
            public int sekil = -1;
            public int donus;
            public int durum;
            public bool agir;
            public Vector3 ruzgarYon = Vector3.forward;
            public float tohum;
            public Vector3 dokunma;
            public float sapX, sapZ, sapAlt, sapUst;
            public bool sapVar;
            public bool izin;
            public string tasAck = "";
            public float ek, ol = 1f, kal = 1f, dalga = 1f;
            public int ruz;                                  // ruzgar seviyesi (0 yok)
            public Vector3 ucV, ucW;                         // firlatilmis bayragin ucusu (kendimiz hesapliyoruz)
            public bool ucuyor, ucusKumas;
            public bool ayak, ayakYerde;
            public GameObject ayakGo;
            public float ayakBoy = -1f;
            public GameObject tirman;
            public string tirmanImza = "";
            public int tirmanKatman;
            public bool tirmanYer, tirmanArac;
            public Transform takipHedef;
            public Vector3 takipLokPos, takipSon;
            public Quaternion takipLokRot = Quaternion.identity;
            public float hareketZaman = -10f;
            public float can = BayrakCanMax;                 // Gorebox: sahibinin gonderdigi can
            public readonly List<GameObject> delikler = new List<GameObject>();
        }
        private readonly Dictionary<int, Uzak> uzaklar = new Dictionary<int, Uzak>();
        private readonly HashSet<Renderer> uzakRendererlar = new HashSet<Renderer>();

        private static bool OzelOdada()
        {
            try
            {
                return Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.CurrentRoom != null && !Photon.Pun.PhotonNetwork.CurrentRoom.IsVisible;
            }
            catch (Exception) { return false; }
        }

        // rig'in el kemigi (VRRig.leftHand/rightHand.rigTarget)
        private Transform RigEl(Component rig, int h)
        {
            if (rig == null) return null;
            try
            {
                object map = UyeAl(rig, rig.GetType(), h == 0 ? "leftHand" : "rightHand");
                if (map == null) return null;
                return UyeAl(map, map.GetType(), "rigTarget") as Transform;
            }
            catch (Exception) { return null; }
        }

        private Component YerelRig()
        {
            try
            {
                if (tTagger == null) return null;
                object inst = UyeAl(null, tTagger, "Instance");
                if (inst == null) return null;
                return UyeAl(inst, tTagger, "offlineVRRig") as Component;
            }
            catch (Exception) { return null; }
        }

        private static string F7(Vector3 p, Quaternion q)
        {
            return string.Join(",", new string[] { F(p.x), F(p.y), F(p.z), F(q.x), F(q.y), F(q.z), F(q.w) });
        }

        // gonderilecek durum: surum|bayrak|ayna|sap|omuz|durum|poz
        // durum: 0 sirtta, 1 sol elde, 2 sag elde, 3 saplanmis
        private string AgVeri()
        {
            if (!gizli || yerelKopya == null || yerelRgo == null)
            {
                return null;
            }
            string id = AgKimlik(secili);
            string fl = Bayrakliklar().Substring(0, 4);
            int sap = sapNo == SapOzel ? 0 : sapNo;
            int dur = tasiyan >= 0 ? 5 : ucuyor ? 6 : sapli ? (yerde ? 4 : 3) : (tutuluyor && tutanH >= 0 ? tutanH + 1 : 0);
            string poz = "";
            if (dur == 6)
            {
                if (string.IsNullOrEmpty(ucusAgPoz)) UcusAgKaydet();
                poz = ucusAgPoz;
            }
            else if (dur == 3 || dur == 4)
                poz = F7(yerelRgo.position, yerelRgo.rotation) + "," + F(yerelRgo.lossyScale.x);
            else if (dur == 1 || dur == 2)
            {
                Transform el = RigEl(YerelRig(), dur - 1);
                if (el == null) dur = 0;
                else
                {
                    Vector3 lp = el.InverseTransformPoint(yerelRgo.position);
                    Quaternion lr = Quaternion.Inverse(el.rotation) * yerelRgo.rotation;
                    // el donerken her an degismesin: 1 cm / 2 derece adimlarla
                    lp = new Vector3(Mathf.Round(lp.x * 100f) / 100f, Mathf.Round(lp.y * 100f) / 100f, Mathf.Round(lp.z * 100f) / 100f);
                    Vector3 e = lr.eulerAngles;
                    lr = Quaternion.Euler(Mathf.Round(e.x / 2f) * 2f, Mathf.Round(e.y / 2f) * 2f, Mathf.Round(e.z / 2f) * 2f);
                    poz = F7(lp, lr);
                }
            }
            return "1|" + id + "|" + fl + "|" + sap + "|" + (sirtSol ? 1 : 0) + "|" + dur + "|" + poz + "|" + donus + "|" + (alinabilir && !kilit ? 1 : 0) + "|" + tasiyan + "|" + tasAck + "|0|" + F(YerelEk) + "|" + F(YerelOl) + "|" + (dengeAyak ? 1 : 0) + "|" + F(YerelKalin) + "|" + F(DalgaSeviye) + "|" + ruzgarSeviye + "|" + (kilit ? 1 : 0) + "|" + Mathf.CeilToInt(bayrakCan);
        }

        private void AgGuncelle()
        {
            if (Time.time - agZaman < 0.25f) return;
            agZaman = Time.time;
            bool ozel = OzelOdada();
            try { TasimaDinle(ozel); } catch (Exception e) { Logger.LogWarning("Flag carry error: " + e.Message); }
            string v = ozel ? AgVeri() : null;
            if (v != agSon)
            {
                try
                {
                    ExitGames.Client.Photon.Hashtable h = new ExitGames.Client.Photon.Hashtable();
                    h[AgAnahtar] = v;
                    if (Photon.Pun.PhotonNetwork.LocalPlayer != null) Photon.Pun.PhotonNetwork.LocalPlayer.SetCustomProperties(h);
                }
                catch (Exception e) { Logger.LogWarning("Flag sync error: " + e.Message); }
                agSon = v;
            }
            try { UzaklariGuncelle(ozel); } catch (Exception e) { Logger.LogWarning("Remote flags error: " + e.Message); }
        }

        private void UzaklariGuncelle(bool ozel)
        {
            HashSet<int> gorulen = new HashSet<int>();
            if (ozel)
            {
                foreach (Photon.Realtime.Player p in Photon.Pun.PhotonNetwork.PlayerListOthers)
                {
                    if (p == null || p.CustomProperties == null) continue;
                    object o;
                    if (!p.CustomProperties.TryGetValue(AgAnahtar, out o)) continue;
                    string v = o as string;
                    if (string.IsNullOrEmpty(v)) continue;
                    gorulen.Add(p.ActorNumber);
                    Uzak u;
                    if (!uzaklar.TryGetValue(p.ActorNumber, out u))
                    {
                        u = new Uzak();
                        u.aktor = p.ActorNumber;
                        u.tohum = UnityEngine.Random.Range(0f, 100f);
                        uzaklar[p.ActorNumber] = u;
                    }
                    if (u.veri != v) { u.veri = v; u.degisti = true; }
                }
            }
            List<int> sil = new List<int>();
            foreach (var kv in uzaklar) if (!gorulen.Contains(kv.Key)) sil.Add(kv.Key);
            foreach (int k in sil) { UzakSil(uzaklar[k]); uzaklar.Remove(k); }
            foreach (var kv in uzaklar)
            {
                try { UzakUygula(kv.Value); }
                catch (Exception e) { Logger.LogWarning("Remote flag " + kv.Key + ": " + e.Message); kv.Value.degisti = false; }
            }
        }

        private void UzakSil(Uzak u)
        {
            if (u.smr != null) uzakRendererlar.Remove(u.smr);
            if (u.kayit != null && u.kayit.mat != null) Destroy(u.kayit.mat);
            if (u.kopya != null) Destroy(u.kopya);
            if (u.mesh != null) Destroy(u.mesh);
            AyakSil(ref u.ayakGo); u.ayakBoy = -1f; u.ayakYerde = false;
            u.tirman = null; u.tirmanImza = ""; u.tirmanYer = false;   // kopyayla birlikte silinir
            u.kopya = null; u.mesh = null; u.smr = null; u.kayit = null; u.rgo = null; u.boing = null;
            u.dokuKod = ""; u.sekil = -1; u.agir = false;
        }

        // sahne taramasi (FindObjectsOfType) pahali: aktor -> rig haritasi tutulur, en fazla 2 sn'de bir taranir
        private readonly Dictionary<int, Component> rigHarita = new Dictionary<int, Component>();
        private float rigTaraZaman = -100f;
        private Component RigBul(int aktor)
        {
            if (tVRRig == null) tVRRig = Type.GetType("VRRig, Assembly-CSharp");
            if (tVRRig == null) return null;
            Component c;
            if (rigHarita.TryGetValue(aktor, out c) && c != null && RigAktor(c) == aktor) return c;   // havuzdaki rig baskasina gecmis olabilir
            if (Time.time - rigTaraZaman < 2f) return null;
            rigTaraZaman = Time.time;
            rigHarita.Clear();
            foreach (UnityEngine.Object o in FindObjectsOfType(tVRRig))
            {
                Component rig = o as Component;
                if (rig == null) continue;
                int a = RigAktor(rig);
                if (a >= 0) rigHarita[a] = rig;
            }
            return rigHarita.TryGetValue(aktor, out c) ? c : null;
        }

        private int RigAktor(Component rig)
        {
            try
            {
                object off = UyeAl(rig, tVRRig, "isOfflineVRRig");
                if (off is bool && (bool)off) return -1;
                object np = UyeAl(rig, tVRRig, "OwningNetPlayer");
                if (np == null) return -1;
                object an = UyeAl(np, np.GetType(), "ActorNumber");
                return an is int ? (int)an : -1;
            }
            catch (Exception) { return -1; }
        }

        private static Transform AltBul(Transform kok, string ad)
        {
            foreach (Transform t in kok.GetComponentsInChildren<Transform>(true)) if (t.name == ad) return t;
            return null;
        }

        private bool YerelMatHazirla()
        {
            if (yerelMat != null) return true;
            Material src = KaynakMateryal();
            if (src == null) return false;
            yerelMat = new Material(src);
            yerelMat.name = "FlagPrideStandard_Local";
            if (yerelMat.HasProperty("_BaseColor")) yerelMat.SetColor("_BaseColor", Color.white);
            if (yerelMat.HasProperty("_Color")) yerelMat.SetColor("_Color", Color.white);
            yerelMat.SetFloat("_BaseMap_AtlasSlice", 276f);
            return true;
        }

        private static float[] Sayilar(string s)
        {
            if (string.IsNullOrEmpty(s)) return new float[0];
            string[] p = s.Split(',');
            float[] f = new float[p.Length];
            for (int i = 0; i < p.Length; i++)
                float.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f[i]);
            return f;
        }

        private void UzakUygula(Uzak u)
        {
            string[] p = u.veri.Split('|');
            if (p.Length < 7 || p[0] != "1") return;
            string id = p[1], fl = p[2].PadRight(4, '0');
            int sap = 0, taraf = 0, dur = 0;
            int.TryParse(p[3], out sap); int.TryParse(p[4], out taraf); int.TryParse(p[5], out dur);
            float[] poz = Sayilar(p[6]);
            if (sap < 0 || sap > 14) sap = 0;
            int dn = 0;
            if (p.Length >= 8) int.TryParse(p[7], out dn);
            dn = ((dn % 360) + 360) % 360;
            int izin = 0, tasAktor = -1;
            if (p.Length >= 9) int.TryParse(p[8], out izin);
            if (p.Length >= 10) int.TryParse(p[9], out tasAktor);
            u.izin = izin == 1;
            u.tasAck = p.Length >= 11 ? p[10] : "";
            float ek = 0f, ol = 1f; int ayk = 0;
            if (p.Length >= 13) float.TryParse(p[12], NumberStyles.Float, CultureInfo.InvariantCulture, out ek);
            if (p.Length >= 14 && !float.TryParse(p[13], NumberStyles.Float, CultureInfo.InvariantCulture, out ol)) ol = 1f;
            if (p.Length >= 15) int.TryParse(p[14], out ayk);
            float kal = 1f;
            if (p.Length >= 16 && !float.TryParse(p[15], NumberStyles.Float, CultureInfo.InvariantCulture, out kal)) kal = 1f;
            kal = Mathf.Clamp(kal, 0.5f, 6f);
            float dlg = 1f;
            if (p.Length >= 17 && !float.TryParse(p[16], NumberStyles.Float, CultureInfo.InvariantCulture, out dlg)) dlg = 1f;
            u.dalga = Mathf.Clamp(dlg, 0f, 3f);
            int rz = 0;
            if (p.Length >= 18) int.TryParse(p[17], out rz);
            u.ruz = Mathf.Clamp(rz, 0, 3);
            ek = Mathf.Clamp(ek, 0f, SapEnUzunM / BirimM);
            ol = Mathf.Clamp(ol, 0.3f, 20f);
            u.ayak = ayk == 1;

            // oyuncunun gorillasi
            if ((u.rig == null && Time.time - u.rigZaman > 1.5f) || (dur != 3 && dur != 4 && Time.time - u.rigZaman > 5f))
            {
                u.rigZaman = Time.time;
                Component rig = RigBul(u.aktor);
                if (rig != u.rig) { u.rig = rig; u.slot = null; }
            }
            if (u.rig != null && u.slot == null) u.slot = AltBul(u.rig.transform, "body_AnchorBackRight_StowSlot");

            int idx = BayrakBul(id);
            int sk = 0;
            if (idx >= 0 && idx < kategoriler.Count && kategoriler[idx] == 0) sk = SekilAdindan(isimler[idx]);

            // kopyayi kur (yoksa ya da sekil degistiyse)
            if (u.kopya == null || u.sekil != sk || u.donus != dn || Mathf.Abs(u.ek - ek) > 0.001f || Mathf.Abs(u.ol - ol) > 0.001f || Mathf.Abs(u.kal - kal) > 0.001f)
            {
                UzakSil(u);
                if (!YerelMatHazirla()) return;
                GomuluSablonYukle();
                Type tBones = Type.GetType("BoingKit.BoingBones, BoingKit");
                if (tBones == null) return;
                int katman = u.slot != null ? u.slot.gameObject.layer : 0;
                u.kopya = new GameObject("GorillaFlag_Remote_" + u.aktor);
                u.kopya.layer = katman;
                int eskiSekil = sekil;
                try { sekil = sk; u.mesh = FizikMesh(ek, ol, kal); }
                finally { sekil = eskiSekil; }
                u.sekil = sk;
                u.donus = dn;
                u.ek = ek; u.ol = ol; u.kal = kal;
                Component bc; Transform a, f; SkinnedMeshRenderer smr;
                u.rgo = IskeletOlustur(tBones, u.kopya.transform, katman, u.mesh, yerelMat, null, out bc, out a, out f, out smr, dn, ek, ol);
                u.boing = bc; u.kA = a; u.kF = f; u.smr = smr;
                uzakRendererlar.Add(smr);
                // malzeme kaydi (diger oyuncunun bayragi icin ayri)
                Kayit k = new Kayit();
                k.mat = smr.material;
                k.shader = k.mat.shader;
                k.keywords = k.mat.shaderKeywords;
                k.dilim = k.mat.HasProperty("_BaseMap_AtlasSlice") ? k.mat.GetFloat("_BaseMap_AtlasSlice") : 0f;
                foreach (string pr in new string[] { "_BaseMap", "_BaseMap_Atlas", "_MainTex" })
                    if (k.mat.HasProperty(pr)) k.tex[pr] = k.mat.GetTexture(pr);
                u.kayit = k;
                u.dokuKod = "";
                u.durum = -1;
                UzakSapHesapla(u);
                Logger.LogInfo("Remote flag created for player " + u.aktor);
            }

            // doku: onun bayragi, ayna ayarlari ve sapi (bizim kalite ayarimizla)
            string kod = idx + "|" + fl + "|" + sap + "|" + kalite + "|" + yontem;
            if (kod != u.dokuKod)
            {
                bool a1 = ustX, a2 = ustY, a3 = altX, a4 = altY; int s0 = sapNo;
                try
                {
                    ustX = fl[0] == '1'; ustY = fl[1] == '1'; altX = fl[2] == '1'; altY = fl[3] == '1'; sapNo = sap;
                    u.kayit.uygulanan = "";
                    Uygula(u.kayit, idx >= 0 ? idx : -2, yontem);
                }
                finally { ustX = a1; ustY = a2; altX = a3; altY = a4; sapNo = s0; }
                u.dokuKod = kod;
            }

            // Gorebox cani: sifira inince parcalanir, dolunca delikler kapanir
            float can = BayrakCanMax;
            if (p.Length >= 20 && !float.TryParse(p[19], NumberStyles.Float, CultureInfo.InvariantCulture, out can)) can = BayrakCanMax;
            if (can <= 0f && u.can > 0f && u.kopya != null && u.kopya.activeInHierarchy) ParcaSac(u.smr, idx, Vector3.zero);
            if (can >= BayrakCanMax && u.can < BayrakCanMax) DelikTemizle(u.delikler);
            u.can = can;

            // yerlesim
            if (u == tasinan || (u == tasBekleyen && !u.degisti)) { u.durum = 5; return; }
            if (dur == 9) { u.kopya.SetActive(false); u.durum = 9; u.degisti = false; return; }   // bayragi yok   // elimde / dusuyor: kendim yerlestiriyorum
            if (dur == 5)
            {
                if (tasAktor == BenimAktor() || !UzakTasinanYerlestir(u, tasAktor)) { if (tasAktor != BenimAktor()) u.kopya.SetActive(false); }
                u.durum = 5;
                u.degisti = false;
                return;
            }
            if ((dur == 3 || dur == 4) && poz.Length >= 8)
            {
                if (u.kopya.transform.parent != null || u.durum != dur || u.degisti)
                {
                    u.kopya.transform.SetParent(null, false);
                    u.kopya.transform.localPosition = Vector3.zero;
                    u.kopya.transform.localRotation = Quaternion.identity;
                    u.kopya.transform.localScale = Vector3.one;
                    u.rgo.position = new Vector3(poz[0], poz[1], poz[2]);
                    u.rgo.rotation = new Quaternion(poz[3], poz[4], poz[5], poz[6]);
                    u.rgo.localScale = Vector3.one * poz[7];
                    if (u.kA != null && u.kF != null)
                    {
                        Vector3 kd = u.kF.position - u.kA.position; kd.y = 0f;
                        u.ruzgarYon = kd.sqrMagnitude > 1e-6f ? kd.normalized : Vector3.forward;
                    }
                    UzakTakipAyarla(u, dur == 3);   // araba vb. ustundeyse aradaki karelerde de onunla gitsin
                }
                u.kopya.SetActive(true);
            }
            else if (dur == 6 && poz.Length >= 14)
            {
                // firlatildi: kalkis (ya da sekme) durumundan sonrasini UzakKare hesaplar
                if (u.kopya.transform.parent != null || u.durum != dur || u.degisti)
                {
                    u.kopya.transform.SetParent(null, false);
                    u.kopya.transform.localPosition = Vector3.zero;
                    u.kopya.transform.localRotation = Quaternion.identity;
                    u.kopya.transform.localScale = Vector3.one;
                    u.rgo.position = new Vector3(poz[0], poz[1], poz[2]);
                    u.rgo.rotation = new Quaternion(poz[3], poz[4], poz[5], poz[6]);
                    u.rgo.localScale = Vector3.one * poz[7];
                    u.ucV = new Vector3(poz[8], poz[9], poz[10]);
                    u.ucW = new Vector3(poz[11], poz[12], poz[13]);
                    u.ucuyor = true;
                    u.takipHedef = null;
                }
                u.kopya.SetActive(true);
            }
            else if ((dur == 1 || dur == 2) && poz.Length >= 7 && RigEl(u.rig, dur - 1) != null)
            {
                Transform el = RigEl(u.rig, dur - 1);
                if (u.kopya.transform.parent != el)
                {
                    u.kopya.transform.SetParent(el, false);
                    u.kopya.transform.localPosition = Vector3.zero;
                    u.kopya.transform.localRotation = Quaternion.identity;
                    u.kopya.transform.localScale = Vector3.one;
                }
                u.rgo.position = el.TransformPoint(new Vector3(poz[0], poz[1], poz[2]));
                u.rgo.rotation = el.rotation * new Quaternion(poz[3], poz[4], poz[5], poz[6]);
                Vector3 ls = u.slot != null ? u.slot.lossyScale : Vector3.one;
                Vector3 es = el.lossyScale;
                u.rgo.localScale = new Vector3(kayitOlcek.x * ls.x / Mathf.Max(es.x, 1e-4f), kayitOlcek.y * ls.y / Mathf.Max(es.y, 1e-4f), kayitOlcek.z * ls.z / Mathf.Max(es.z, 1e-4f));
                u.kopya.SetActive(true);
            }
            else if (u.slot != null)
            {
                if (u.kopya.transform.parent != u.slot)
                {
                    u.kopya.transform.SetParent(u.slot, false);
                    u.kopya.transform.localPosition = Vector3.zero;
                    u.kopya.transform.localRotation = Quaternion.identity;
                    u.kopya.transform.localScale = Vector3.one;
                }
                if (taraf == 1) { u.rgo.localPosition = SolPos; u.rgo.localRotation = SolRot; }
                else { u.rgo.localPosition = kayitPos; u.rgo.localRotation = kayitRot; }
                u.rgo.localScale = kayitOlcek;
                u.kopya.SetActive(true);
                dur = 0;
            }
            else
            {
                u.kopya.SetActive(false);   // gorillasi henuz bulunamadi
            }
            u.durum = dur;
            u.degisti = false;
        }

        // her karede: saplanmis uzak bayraklarda ruzgar + dokunma
        private void UzakKare()
        {
            foreach (var kv in uzaklar)
            {
                Uzak u = kv.Value;
                if (u.durum == 6 && u.rgo != null && u != tasinan)
                {
                    // firlatilmis bayrak: kalkis durumundan ucusu burada da hesapla (akici gorunsun)
                    if (u.ucuyor)
                    {
                        if (!u.sapVar) UzakSapHesapla(u);
                        if (u.sapVar)
                        {
                            float dt = Mathf.Min(Time.deltaTime, 0.05f);
                            RaycastHit uh; bool uc;
                            if (UcusAdim(u.rgo, new Vector3(u.sapX, u.sapAlt, u.sapZ), new Vector3(u.sapX, u.sapUst, u.sapZ), ref u.ucV, ref u.ucW, dt, out uh, out uc)) u.ucuyor = false;
                        }
                    }
                    if (u.boing != null)
                    {
                        if (u.agir) { BoingAgirAyar(u.boing, false); u.agir = false; }
                        BoingYercekimiAyar(u.boing, u.ucuyor ? UcusKumasKuvvet(u.ucV, Time.time + u.tohum) : Vector3.zero);
                        u.ucusKumas = true;
                    }
                    continue;
                }
                if (u.ucusKumas && u.boing != null) { u.ucusKumas = false; BoingYercekimiAyar(u.boing, Vector3.zero); }
                if (u.boing == null || u.kA == null || u.kF == null) continue;
                bool sap = u.durum == 3;
                if (sap != u.agir) { BoingAgirAyar(u.boing, sap); u.agir = sap; if (!sap) { BoingYercekimiAyar(u.boing, Vector3.zero); u.dokunma = Vector3.zero; } }
                if (!sap) continue;
                u.dokunma = DokunmaGuncelle(u.dokunma, u.kA, u.kF);
                BoingYercekimiAyar(u.boing, RuzgarKuvvet(Time.time + u.tohum, u.ruzgarYon, u.dalga, u.ruz) + u.dokunma);
            }
        }

        // ---- oyunun el / tutma bilgisine yansima ile eris (oyun koduna bagimlilik olmasin) ----
        // yansima aramalari pahali: her (tip, ad) icin bir kez bul, sonra hep onbellekten kullan
        private static readonly Dictionary<Type, Dictionary<string, System.Reflection.MemberInfo>> uyeOnbellek = new Dictionary<Type, Dictionary<string, System.Reflection.MemberInfo>>();
        private static object UyeAl(object o, Type t, string ad)
        {
            Dictionary<string, System.Reflection.MemberInfo> d;
            if (!uyeOnbellek.TryGetValue(t, out d)) { d = new Dictionary<string, System.Reflection.MemberInfo>(); uyeOnbellek[t] = d; }
            System.Reflection.MemberInfo mi;
            if (!d.TryGetValue(ad, out mi))
            {
                const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
                mi = t.GetField(ad, bf);
                if (mi == null) mi = t.GetProperty(ad, bf);
                d[ad] = mi;
            }
            var f = mi as System.Reflection.FieldInfo;
            if (f != null) return f.GetValue(o);
            var p = mi as System.Reflection.PropertyInfo;
            if (p != null) return p.GetValue(o, null);
            return null;
        }

        private Type tTagger, tPoller;
        private bool yansimaHazir, yansimaUyari;

        private Transform El(int h)
        {
            try
            {
                if (!yansimaHazir)
                {
                    yansimaHazir = true;
                    tTagger = Type.GetType("GorillaTagger, Assembly-CSharp");
                    tPoller = Type.GetType("ControllerInputPoller, Assembly-CSharp");
                }
                if (tTagger == null) return null;
                object inst = UyeAl(null, tTagger, "Instance");
                if (inst == null) return null;
                return UyeAl(inst, tTagger, h == 0 ? "leftHandTransform" : "rightHandTransform") as Transform;
            }
            catch (Exception) { return null; }
        }

        private bool Tutuyor(int h)
        {
            try
            {
                if (tPoller == null) return false;
                object inst = UyeAl(null, tPoller, "instance");
                if (inst == null) return false;
                object b = UyeAl(inst, tPoller, h == 0 ? "leftGrab" : "rightGrab");
                if (b is bool) return (bool)b;
                object f = UyeAl(inst, tPoller, h == 0 ? "leftControllerGripFloat" : "rightControllerGripFloat");
                if (f is float) return (float)f > 0.6f;
            }
            catch (Exception) { }
            return false;
        }

        private static float NoktaDogru(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return Vector3.Distance(p, a + ab * t);
        }

        private void YerelKare()
        {
            if (yerelKopya == null || yerelMesh == null || yerelTaban == null || yerelRgo == null) return;

            // gercek kozmetik takildiysa kopyayi hemen kaldir
            if (yerelKaynak != null && yerelKaynak.gameObject.activeInHierarchy) { YerelKaldir(); return; }

            Vector3 sapAlt = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapAltY, yerelSapZ));
            Vector3 sapUst = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapUstY, yerelSapZ));

            // tutma / birakma
            for (int h = 0; h < 2; h++)
            {
                Transform el = El(h);
                bool g = el != null && Tutuyor(h);
                if (g && !oncekiGrab[h] && !tutuluyor && tasiyan < 0 && !(sapli && kilit) && NoktaDogru(el.position, sapAlt, sapUst) < 0.3f)
                {
                    if (sapli)
                    {
                        // saplanmis bayragi geri al: dunyadaki yerini koruyup oyuncuya bagla
                        Vector3 wp = yerelRgo.position; Quaternion wr = yerelRgo.rotation;
                        SapliCoz();
                        yerelRgo.position = wp; yerelRgo.rotation = wr;
                    }
                    if (ucuyor) { ucuyor = false; devriliyor = false; KopyaSirtaBagla(); }   // havada yakaladin
                    tutuluyor = true;
                    tutanEl = el;
                    tutanH = h;
                    tutHizVar = false;
                    donusSuruyor = false;
                    // sapin alt kismi ele gelsin
                    Vector3 tutNokta = Vector3.Lerp(sapAlt, sapUst, 0.12f);
                    Vector3 yeniPos = yerelRgo.position + (el.position - tutNokta);
                    tutOfsPos = el.InverseTransformPoint(yeniPos);
                    tutOfsRot = Quaternion.Inverse(el.rotation) * yerelRgo.rotation;
                }
                if (!g && oncekiGrab[h] && tutuluyor && tutanEl == el && firlatAcik && tutHizVar && tutHiz.magnitude > FirlatEsik)
                {
                    // firlat
                    tutuluyor = false;
                    tutanEl = null;
                    yerelKopya.transform.SetParent(null, true);
                    ucuyor = true;
                    ucusHiz = tutHiz * FirlatCarpan;
                    ucusAci = tutAci + RastgeleTakla(ucusHiz);
                    ucusSekme = 0;
                    devriliyor = false;
                    ucusZaman = Time.time;
                    UcusAgKaydet();
                    oncekiUcVar = false;
                    Logger.LogInfo("Local flag thrown (" + ucusHiz.magnitude.ToString("0.0") + " m/s)");
                }
                else if (!g && oncekiGrab[h] && tutuluyor && tutanEl == el)
                {
                    tutuluyor = false;
                    tutanEl = null;
                    sirtSol = tarafCfg != null && tarafCfg.Value == 1;   // birakinca secilen omuza doner
                    // isinlanmasin: bulundugu yerden sirta yumusakca donsun
                    donusBasPos = yerelRgo.position;
                    donusBasRot = yerelRgo.rotation;
                    donusBasZaman = Time.time;
                    donusSuruyor = true;
                }
                oncekiGrab[h] = g;
            }
            if (!yansimaUyari && tTagger == null && yansimaHazir) { yansimaUyari = true; Logger.LogWarning("Local flag: hand info not found, grabbing disabled."); }

            if (tutuluyor && tutanEl != null)
            {
                Transform elKemik = (tutanH >= 0 && tutKVar[tutanH]) ? RigEl(YerelRig(), tutanH) : null;
                if (elKemik != null)
                {
                    // gercek bayrak gibi gorillanin el kemigine bagli: avucun icinde durur
                    yerelRgo.position = elKemik.TransformPoint(tutKPos[tutanH]);
                    yerelRgo.rotation = elKemik.rotation * tutKRot[tutanH];
                }
                else if (tutanH >= 0 && tutVar[tutanH])
                {
                    // oyundaki gibi sabit tutus: sap yumrugun icinden gecer
                    yerelRgo.position = tutanEl.TransformPoint(tutPos[tutanH]);
                    yerelRgo.rotation = tutanEl.rotation * tutRot[tutanH];
                }
                else
                {
                    yerelRgo.position = tutanEl.TransformPoint(tutOfsPos);
                    yerelRgo.rotation = tutanEl.rotation * tutOfsRot;
                }

                // firlatma icin elin (bayragin) hizi
                {
                    float dth = Mathf.Max(Time.deltaTime, 0.001f);
                    Vector3 pc = yerelRgo.position;
                    Vector3 anlik = tutHizVar ? (pc - tutOnceki) / dth : Vector3.zero;
                    if (anlik.magnitude > 40f) anlik = Vector3.zero;
                    tutHiz = tutHizVar ? Vector3.Lerp(tutHiz, anlik, 0.5f) : anlik;
                    Vector3 wAn = tutHizVar ? AciHiz(tutRotOnceki, yerelRgo.rotation, dth) : Vector3.zero;
                    tutAci = tutHizVar ? Vector3.Lerp(tutAci, wAn, 0.5f) : Vector3.zero;
                    tutRotOnceki = yerelRgo.rotation;
                    tutOnceki = pc; tutHizVar = true;
                }

                // yere saplama: Y/B basili + sapin ucu yere sert vuruldu
                Vector3 alt = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapAltY, yerelSapZ));
                Vector3 ust = yerelRgo.TransformPoint(new Vector3(yerelSapX, yerelSapUstY, yerelSapZ));
                Vector3 asagi = (alt - ust).normalized;
                float dtu = Mathf.Max(Time.deltaTime, 0.001f);
                float hizUc = oncekiUcVar ? Vector3.Dot((alt - oncekiUc) / dtu, asagi) : 0f;
                oncekiUc = alt; oncekiUcVar = true;
                if (hizUc > 30f) hizUc = 0f;   // isinlanma
                if (saplamaAcik && hizUc >= SaplamaHizi && XBasili())
                {
                    Vector3 bas = Vector3.Lerp(alt, ust, 0.3f);
                    float uz = Vector3.Distance(bas, alt) + 0.1f;
                    RaycastHit zemin;
                    if (ZeminBul(bas, asagi, uz, out zemin)) Sapla(zemin, hizUc, asagi);
                }
            }
            else if (ucuyor)
            {
                UcusKare();
            }
            else if (sapli)
            {
                oncekiUcVar = false;
                // odadan cikinca / haritadan uzaklasinca bayrak sirta doner
                TasiyanKare();
                Transform el0 = El(0);
                bool uzak = donusMesafe > 0f && el0 != null && Vector3.Distance(el0.position, yerelRgo.position) > donusMesafe;
                if (OdaAdi() != sapliOda || uzak)
                {
                    tasiyan = -1;
                    SapliCoz();
                    SirtaKoy();
                    Logger.LogInfo("Local flag returned to the back (left the room / map).");
                }
            }
            else
            {
                oncekiUcVar = false;
                SirtaKoy();
                if (donusSuruyor)
                {
                    float k = (Time.time - donusBasZaman) / 0.3f;
                    if (k >= 1f) donusSuruyor = false;
                    else
                    {
                        k = k * k * (3f - 2f * k);
                        yerelRgo.position = Vector3.Lerp(donusBasPos, yerelRgo.position, k);
                        yerelRgo.rotation = Quaternion.Slerp(donusBasRot, yerelRgo.rotation, k);
                    }
                }
            }

            SapliRuzgar();
            if (yerelBoing) return;   // kumasi oyunun kendi yay fizigi (BoingBones) sallar

            // hareket hizi -> ruzgar
            float dt = Mathf.Max(Time.deltaTime, 0.001f);
            Vector3 sp = yerelRgo.TransformPoint(new Vector3(yerelSapX, (yerelSapAltY + yerelSapUstY) * 0.5f, yerelSapZ));
            Vector3 hiz = oncekiVar ? (sp - oncekiSapPos) / dt : Vector3.zero;
            oncekiSapPos = sp; oncekiVar = true;
            if (hiz.magnitude > 30f) hiz = Vector3.zero;   // isinlanma
            hizYumusak = Vector3.Lerp(hizYumusak, hiz, Mathf.Clamp01(dt * 5f));
            Vector3 ruzgar = yerelRgo.InverseTransformDirection(-hizYumusak);
            float hizB = Mathf.Min(hizYumusak.magnitude, 6f);
            float genlik = 0.55f + hizB * 0.3f;
            float bukZ = Mathf.Clamp(ruzgar.z * 0.1f, -0.7f, 0.7f) * yerelMaks;
            float bukY = Mathf.Clamp(ruzgar.y * 0.05f, -0.4f, 0.4f) * yerelMaks;

            float t = Time.time;
            float hizF = 3.2f + hizB * 1.2f;
            Quaternion q = Quaternion.AngleAxis(donus, Vector3.up);
            for (int i = 0; i < yerelTaban.Length; i++)
            {
                Vector3 v = yerelTaban[i];
                float f = yerelMesafe[i] / yerelMaks;
                if (f > 0f)
                {
                    v.z += Mathf.Sin(t * hizF - f * 4.5f) * 0.22f * yerelMaks * f * genlik + bukZ * f * f;
                    v.y += Mathf.Sin(t * hizF * 0.75f - f * 4.0f) * 0.05f * yerelMaks * f * genlik + bukY * f * f;
                    if (donus != 0)
                    {
                        Vector3 p = new Vector3(yerelSapX, 0f, yerelSapZ);
                        Vector3 o = v - p; float yy = o.y; o.y = 0f;
                        o = q * o; o.y = yy;
                        v = o + p;
                    }
                }
                yerelCalisma[i] = v;
            }
            yerelMesh.vertices = yerelCalisma;
            yerelMesh.RecalculateNormals();
            yerelMesh.RecalculateBounds();
        }
        // 0-2: moddaki hazir bayraklar, 3: Custom (kendi resimlerin, Flags klasorunun kendisi), 4+: kullanicinin klasorleri
        private static readonly string[] KatAdlari = { "Countries", "Pride", "Regions", "Custom", "Organizations" };
        private static readonly string[] KatGorunAd = { "Countries", "Pride", "Territories", "Custom", "Organizations" };
        private static readonly string[] KatOnek = { "countries/", "pride/", "regions/", "custom/", "organizations/" };
        private const int KatOzel = 3;   // Custom
        private const int KatOrg = 4;    // Organizations: DLL'e gomulu kurulus bayraklari (BM, NATO, ...)
        private static int SabitKat { get { return KatAdlari.Length; } }   // sabit kategoriler; sonrakiler kullanicinin
        // moddaki hazir bayraklarin kategorisi mi (Countries, Pride, Regions, Organizations): silinmez, ada gore gonderilir
        private static bool HazirKat(int k) { return k < KatOzel || k == KatOrg; }
        // menudeki kategori sirasi: Custom, Countries, Territories, Pride, sonra kullanicinin kategorileri
        private int KatSira(int p)
        {
            int[] sabit = { KatOzel, 0, 2, KatOrg, 1 };   // Custom, Ulkeler, Bolgeler, Kuruluslar, Pride
            return p < sabit.Length ? sabit[p] : p;
        }
        private readonly List<string> katListesi = new List<string>();   // 0,1 sabit; 2+ kullanicinin klasorleri
        private float chipKaydir;
        private bool chipSurukle;
        private float chipSurukleX;
        private bool yeniKatAcik;
        private string yeniKatAd = "";
        private int silBekleyen = -1;
        private float silZaman;
        private int katSilBekleyen = -1;
        private float katSilZaman;
        private int aktifKat = 0;
        private string arama = "";
        private bool aramaOdak;
        private int secili = -1;
        private int yontem = 0;
        private float zamanlayici;

        // Orijinal atlas dilimi (sablon)
        private readonly Dictionary<int, Color32[]> sablonlar = new Dictionary<int, Color32[]>();
        private bool sablonSrgb = true;
        private TextureWrapMode orijinalWrap = TextureWrapMode.Clamp;
        private FilterMode orijinalFilter = FilterMode.Bilinear;
        private int orijinalAniso = 1;

        private bool menuAcik;
        private CursorLockMode eskiKilit;
        private bool eskiGorunur;
        private Vector2 kaydirma;
        private float barTut;
        private string durum = "";

        private static string Temizle(string s)
        {
            return (s ?? "").ToLower().Replace("_", "").Replace(" ", "");
        }

        private string Bayrakliklar()
        {
            return "" + (ustX ? 1 : 0) + (ustY ? 1 : 0) + (altX ? 1 : 0) + (altY ? 1 : 0) + "p" + sapNo + (sapNo == SapOzel ? "_" + sapOzelSurum : "") + "q" + kalite;
        }

        // ---------- Yasam dongusu ----------

        void Start()
        {
            ben = this;
            DosyaSureciHazirla();   // dosya penceresi icin arka plan sureci (ilk acilis hizli olsun)
            temaCfg = Config.Bind("General", "Theme", 3, "Menu color index (0-13, 3 = red)");
            kozCfg = Config.Bind("General", "FlagCosmetic", 3, "Variant (0 Pride Flag, 1 Trans Pride Flag, 3 None)");
            kozmetik = Mathf.Clamp(kozCfg.Value, 0, 3);
            gizliCfg = Config.Bind("General", "Private", false, "Private flag: only you see it (independent of Variant)");
            gizli = gizliCfg.Value;
            if (kozmetik == 2) { kozmetik = 3; kozCfg.Value = 3; gizli = true; gizliCfg.Value = true; }   // eski surumden: Private variant -> Private acik
            pozCfg = Config.Bind("General", "LocalFlagPoseRight", "", "Saved back pose of the local-only flag (learned automatically)");
            PozYukle();
            kaliteCfg = Config.Bind("General", "TextureQuality", 1, "Flag texture quality (0 Normal 256, 1 High 512, 2 Ultra 1024)");
            saplamaCfg = Config.Bind("General", "PlantFlag", true, "Planting the flag into the ground / walls is enabled");
            // eski surumlerden kalan "kapali" degeri bir kez acilir (varsayilan acik)
            var surumCfg = Config.Bind("General", "SettingsVersion", 0, "Internal, do not change");
            if (surumCfg.Value < 1) { saplamaCfg.Value = true; surumCfg.Value = 1; }
            saplamaAcik = saplamaCfg.Value;
            ruzgarCfg = Config.Bind("General", "PlantedWind", 1f, "Wind strength on a planted flag (0 = none, 1 = normal, 2 = strong)");
            menuTusCfg = Config.Bind("Keybinds", "MenuKey", 0x46, "Windows virtual key code that opens/closes the menu (70 = F)");
            menuTus = menuTusCfg.Value > 0 && menuTusCfg.Value < 255 ? menuTusCfg.Value : 0x46;
            saplaTusCfg = Config.Bind("Keybinds", "PlantButton", 0, "Button held to plant the flag (0 Y/B, 1 X/A, 2 Y, 3 B, 4 X, 5 A)");
            saplaTus = Mathf.Clamp(saplaTusCfg.Value, 0, SaplaTusAdlari.Length - 1);
            saplaYuvaCfg[0] = Config.Bind("Keybinds", "PlantButton1", 1, "First button to plant the flag (0 none, 1 Y, 2 X, 3 B, 4 A)");
            saplaYuvaCfg[1] = Config.Bind("Keybinds", "PlantButton2", 3, "Second button to plant the flag (0 none, 1 Y, 2 X, 3 B, 4 A)");
            for (int y = 0; y < 2; y++) saplaYuva[y] = Mathf.Clamp(saplaYuvaCfg[y].Value, 0, KolTusAdlari.Length - 1);
            ekAydinlikCfg = Config.Bind("General", "WallScreenLight", false, "Wall screen appearance (false dark, true light)");
            saplamaHiziCfg = Config.Bind("General", "PlantStrength", 1.5f, "How fast (m/s) the pole tip must hit to plant: 0.8 softest, 1.1 soft, 1.5 normal, 2.2 hard, 3.0 hardest");
            SaplamaHizi = Mathf.Clamp(saplamaHiziCfg.Value, 0.3f, 6f);
            yaziStiliCfg = Config.Bind("General", "WallScreenFont", 0, "Wall screen font (0 original, 1 Gorilla Tag, 2 modern)");
            sapBoyCfg = Config.Bind("General", "PoleLength", SapVarsayilanM, "Total pole length in meters (set from the button on the pole)");
            bayrakBoyCfg = Config.Bind("General", "FlagSize", BayrakVarsayilanM, "Flag cloth height in meters, up to the pole length");
            dengeAyakCfg = Config.Bind("General", "BaseStand", false, "Round base stand under a planted flag");
            var surum2Cfg = Config.Bind("General", "SettingsVersion", 0, "Internal, do not change");
            if (surum2Cfg.Value < 2) { dengeAyakCfg.Value = false; surum2Cfg.Value = 2; }   // varsayilan artik kapali
            sapBoyM = Mathf.Clamp(sapBoyCfg.Value, SapVarsayilanM, SapEnUzunM);
            bayrakBoyM = Mathf.Clamp(bayrakBoyCfg.Value, BayrakEnKucukM, BayrakEnBuyukM);
            dengeAyak = dengeAyakCfg.Value;
            sapKalinCfg = Config.Bind("General", "PoleThickness", 1f, "Pole thickness multiplier (longer poles can be thicker)");
            sapKalin = Mathf.Clamp(sapKalinCfg.Value, SapKalinMin, SapKalinMaks);
            sandboxCfg = Config.Bind("General", "GFSandbox", true, "Show the GF Sandbox button on the pole");
            sandboxAcik = sandboxCfg.Value;
            ruzgarSevCfg = Config.Bind("General", "WindBlow", 0, "Steady wind on a planted flag (0 off, 1 light, 2 medium, 3 strong)");
            ruzgarSeviye = Mathf.Clamp(ruzgarSevCfg.Value, 0, 3);
            dikCfg = Config.Bind("General", "UprightPlant", false, "Planted flags stand perfectly upright");
            dikAcik = dikCfg.Value;
            kilitCfg = Config.Bind("General", "LockFlag", false, "Locked planted flag: nobody can pick it up, not even you");
            kilit = kilitCfg.Value;
            firlatCfg = Config.Bind("General", "ThrowFlag", true, "Throw the flag by letting go while swinging, and catch it in the air");
            firlatAcik = firlatCfg.Value;
            yaziStili = Mathf.Clamp(yaziStiliCfg.Value, 0, 2);
            tirmanCfg = Config.Bind("General", "ClimbableFlag", true, "Planted flags are solid and climbable in modded (Utilla) lobbies");
            tirmanAcik = tirmanCfg.Value;
            alinabilirCfg = Config.Bind("General", "OthersCanTake", true, "Other mod users can pick up, carry, drop and plant your planted flag (private rooms only)");
            alinabilir = alinabilirCfg.Value;
            donusMesafeCfg = Config.Bind("General", "ReturnDistance", 60f, "Planted/dropped flag returns to your back when you are this far from it (meters, 0 = never)");
            donusMesafe = Mathf.Max(0f, donusMesafeCfg.Value);
            ekAydinlik = ekAydinlikCfg.Value;
            kalite = Mathf.Clamp(kaliteCfg.Value, 0, 2);
            tarafCfg = Config.Bind("General", "FlagSide", 0, "Shoulder of the Private flag (0 right, 1 left)");
            sirtSol = tarafCfg.Value == 1;
            detayCfg = Config.Bind("General", "FlagDetail", 6, "Flag cloth parts (6 original, 12, 24, 48)");
            detay = Array.IndexOf(DetayDegerleri, detayCfg.Value) >= 0 ? detayCfg.Value : 6;
            sapCfg = Config.Bind("General", "Pole", 0, "Flag pole texture (0 original)");
            sapNo = Mathf.Clamp(sapCfg.Value, 0, SapSon);
            if (File.Exists(SapDosyasi())) SapResimYukle(SapDosyasi(), false);
            else if (sapNo == SapOzel) sapNo = 0;
            tutCfg[0] = Config.Bind("General", "LocalFlagHoldLeft", "", "Left hand hold pose of the local-only flag (learned automatically)");
            tutCfg[1] = Config.Bind("General", "LocalFlagHoldRight", "", "Right hand hold pose of the local-only flag (learned automatically)");
            TutYukle(0); TutYukle(1);
            tutKCfg[0] = Config.Bind("General", "LocalFlagHoldBoneLeft", "", "Left hand hold pose relative to the gorilla hand bone (learned automatically)");
            tutKCfg[1] = Config.Bind("General", "LocalFlagHoldBoneRight", "", "Right hand hold pose relative to the gorilla hand bone (learned automatically)");
            TutKYukle(0); TutKYukle(1);
            temaNo = Mathf.Clamp(temaCfg.Value, 0, Temalar.Length - 1);
            dilCfg = Config.Bind("General", "Language", 0, "Menu language (0 English, 1 Turkish, 2 Spanish, 3 Russian)");
            dil = Mathf.Clamp(dilCfg.Value, 0, DilAdlari.Length - 1);
            PngleriYukle();

            // son oturumdaki secimler (ilk kurulumda hepsi varsayilan)
            flipCfg = Config.Bind("General", "Flips", "1000", "Flag side flips (side1 mirror, side1 upside down, side2 mirror, side2 upside down)");
            donusCfg = Config.Bind("General", "Rotation", 0, "Flag rotation in degrees");
            yontemCfg = Config.Bind("General", "ApplyMethod", 0, "0 Array, 1 Keyword, 2 Shader");
            secCfg = Config.Bind("General", "SelectedFlag", "", "Last selected flag (category/name), empty = original");
            favCfg = Config.Bind("General", "Favorites", "", "Favorite flags (category/name, separated by |)");
            foreach (string fa in favCfg.Value.Split('|')) if (fa.Trim() != "") favoriler.Add(fa.Trim());
            string fl = flipCfg.Value ?? "";
            if (fl.Length == 4) { ustX = fl[0] == '1'; ustY = fl[1] == '1'; altX = fl[2] == '1'; altY = fl[3] == '1'; }
            donus = Mathf.Clamp(donusCfg.Value, 0, 360);
            donusDirty = donus != 0;
            yontem = Mathf.Clamp(yontemCfg.Value, 0, 2);
            secili = BayrakBul(secCfg.Value);
            sekil = SekilBul(secili);
        }

        private BepInEx.Configuration.ConfigEntry<string> flipCfg, secCfg;
        private BepInEx.Configuration.ConfigEntry<int> donusCfg, yontemCfg;

        // Custom resmin "parmak izi": resmin icerigi (dosya adi degil). Ayni resim kimde varsa onda ayni bayrak gorunur.
        private readonly Dictionary<Texture, string> ozetler = new Dictionary<Texture, string>();
        private static string ResimOzet(Texture2D t)
        {
            try
            {
                Color32[] px = t.GetPixels32();
                int w = t.width, h = t.height;
                ulong hs = 1469598103934665603UL;
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        Color32 c = px[Mathf.Clamp((int)((y + 0.5f) / 16f * h), 0, h - 1) * w + Mathf.Clamp((int)((x + 0.5f) / 16f * w), 0, w - 1)];
                        foreach (byte b in new[] { (byte)(c.r >> 4), (byte)(c.g >> 4), (byte)(c.b >> 4) }) { hs ^= b; hs *= 1099511628211UL; }
                    }
                hs ^= (ulong)(w * 31 + h); hs *= 1099511628211UL;   // en-boy orani da
                return hs.ToString("x16");
            }
            catch (Exception) { return null; }
        }

        // diger oyunculara gonderilen bayrak kimligi: hazir bayrak -> adi, Custom resim -> "#" + parmak izi
        private string AgKimlik(int i)
        {
            if (i < 0 || i >= kategoriler.Count || i >= bayraklar.Count) return "";
            if (HazirKat(kategoriler[i])) return BayrakAnahtari(i);
            if (i < refler.Count && refler[i] != null)
            {
                for (int j = 0; j < isimler.Count; j++) if (HazirKat(kategoriler[j]) && Anahtar(j) == refler[i]) return BayrakAnahtari(j);
                return "";
            }
            string o;
            return ozetler.TryGetValue(bayraklar[i], out o) && o != null ? "#" + o : "";
        }

        private string BayrakAnahtari(int i)
        {
            if (i < 0 || i >= isimler.Count || i >= kategoriler.Count) return "";
            int k = kategoriler[i];
            return (k >= 0 && k < katListesi.Count ? katListesi[k] : "") + "/" + isimler[i];
        }

        private int BayrakBul(string anahtar)
        {
            if (string.IsNullOrEmpty(anahtar)) return -1;
            if (anahtar[0] == '#')
            {
                string o = anahtar.Substring(1), b;
                for (int i = 0; i < bayraklar.Count && i < kategoriler.Count; i++)
                    if (kategoriler[i] >= KatOzel && ozetler.TryGetValue(bayraklar[i], out b) && b == o) return i;
                return -1;   // bu resim bende yok: duz beyaz
            }
            for (int i = 0; i < isimler.Count; i++) if (BayrakAnahtari(i) == anahtar) return i;
            return -1;
        }

        private void SecimleriKaydet()
        {
            if (flipCfg != null) flipCfg.Value = Bayrakliklar().Substring(0, 4);
            if (yontemCfg != null) yontemCfg.Value = yontem;
            if (secCfg != null) secCfg.Value = BayrakAnahtari(secili);
        }

        private void PngleriYukle()
        {
            string eskiAd = (secili >= 0 && secili < isimler.Count) ? isimler[secili] : null;
            int eskiKat = (secili >= 0 && secili < kategoriler.Count) ? kategoriler[secili] : -1;
            string eskiKatAd = (eskiKat >= 0 && eskiKat < katListesi.Count) ? katListesi[eskiKat] : null;
            string aktifKatAd = (aktifKat >= 0 && aktifKat < katListesi.Count) ? katListesi[aktifKat] : null;

            bayraklar.Clear();
            ozetler.Clear();
            isimler.Clear();
            kategoriler.Clear();
            refler.Clear();
            katListesi.Clear();
            katListesi.AddRange(KatAdlari);
            foreach (var kv in kayitlar) kv.Value.uygulanan = "";
            atlaslar.Clear();
            diziler.Clear();

            string klasor = Path.Combine(Path.GetDirectoryName(Info.Location), "Flags");

            Type tip = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
            var yukle = tip.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]) });

            // 1) DLL'in icine gomulu bayraklar
            System.Reflection.Assembly asm = typeof(Plugin).Assembly;
            foreach (string kaynak in asm.GetManifestResourceNames())
            {
                if (!kaynak.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;

                string govde = kaynak.Substring(0, kaynak.Length - 4);
                int nokta = govde.LastIndexOf('.');
                string ad = govde.Substring(nokta + 1);
                string onek = nokta > 0 ? govde.Substring(0, nokta) : "";
                string katAd = onek.Substring(onek.LastIndexOf('.') + 1).ToLowerInvariant();
                int kat = katAd == "pride" ? 1 : (katAd == "regions" ? 2 : (katAd == "organizations" ? KatOrg : 0));

                using (Stream st = asm.GetManifestResourceStream(kaynak))
                using (MemoryStream ms = new MemoryStream())
                {
                    st.CopyTo(ms);
                    var tex = new Texture2D(2, 2);
                    yukle.Invoke(null, new object[] { tex, ms.ToArray() });
                    tex.name = ad;
                    if (kat == KatOrg) ozetler[tex] = ResimOzet(tex);   // eski surumler kuruluslari "#" + parmak izi ile gonderir
                    bayraklar.Add(tex);
                    isimler.Add(ad);
                    kategoriler.Add(kat);
                    refler.Add(null);
                }
            }

            Dictionary<string, int> hazir = new Dictionary<string, int>();
            for (int i = 0; i < isimler.Count; i++) hazir[Anahtar(i)] = i;

            // 2) Flags klasorundeki PNG'ler -> Custom (kullanicinin kendi kategorileri kaldirildi; alt klasorler okunmaz)
            List<KeyValuePair<string, int>> kaynaklar = new List<KeyValuePair<string, int>>();
            if (Directory.Exists(klasor)) kaynaklar.Add(new KeyValuePair<string, int>(klasor, KatOzel));   // Flags klasorunun kendisi = Custom
            foreach (var kk in kaynaklar)
            {
                string liste = Path.Combine(kk.Key, "flags.txt");
                if (File.Exists(liste))
                {
                    foreach (string satirL in File.ReadAllLines(liste))
                    {
                        string key = satirL.Trim();
                        int h;
                        if (key == "" || !hazir.TryGetValue(key, out h)) continue;
                        bayraklar.Add(bayraklar[h]);
                        isimler.Add(isimler[h]);
                        kategoriler.Add(kk.Value);
                        refler.Add(key);
                    }
                }
                foreach (string dosya in Directory.GetFiles(kk.Key, "*.png"))
                {
                    try
                    {
                        string ad = Path.GetFileNameWithoutExtension(dosya);
                        var tex = new Texture2D(2, 2);
                        yukle.Invoke(null, new object[] { tex, File.ReadAllBytes(dosya) });
                        tex.name = ad;
                        ozetler[tex] = ResimOzet(tex);
                        bayraklar.Add(tex);
                        isimler.Add(ad);
                        kategoriler.Add(kk.Value);
                        refler.Add(null);
                    }
                    catch (Exception ex) { Logger.LogWarning("PNG load error: " + dosya + " " + ex.Message); }
                }
            }

            // kategori + isme gore sirala
            int[] sira = new int[isimler.Count];
            for (int i = 0; i < sira.Length; i++) sira[i] = i;
            string[] adK = isimler.ToArray();
            int[] katK = kategoriler.ToArray();
            Texture2D[] texK = bayraklar.ToArray();
            string[] refK = refler.ToArray();
            Array.Sort(sira, (a, b) =>
            {
                if (katK[a] != katK[b]) return katK[a].CompareTo(katK[b]);
                return string.Compare(adK[a], adK[b], StringComparison.OrdinalIgnoreCase);
            });
            isimler.Clear();
            bayraklar.Clear();
            kategoriler.Clear();
            refler.Clear();
            foreach (int i in sira) { isimler.Add(adK[i]); bayraklar.Add(texK[i]); kategoriler.Add(katK[i]); refler.Add(refK[i]); }

            ThumbHazirla();

            secili = -1;
            if (eskiAd != null)
                for (int i = 0; i < isimler.Count; i++)
                    if (isimler[i] == eskiAd && katListesi[kategoriler[i]] == eskiKatAd) { secili = i; break; }

            aktifKat = 0;
            if (aktifKatAd != null)
                for (int k = 0; k < katListesi.Count; k++)
                    if (katListesi[k] == aktifKatAd) { aktifKat = k; break; }
            Logger.LogInfo(bayraklar.Count + " flags loaded. F: menu, H: list renderers");
        }

        // ---- kasma bekcisi: modun bir karede 6 ms'den fazla surdugu anlari, en yavas bolumle birlikte log'a yazar (10 sn'de en fazla bir kez) ----
        private readonly System.Diagnostics.Stopwatch perfSaat = System.Diagnostics.Stopwatch.StartNew();
        private long perfBas, perfSon, perfEnCok;
        private string perfEnAd = "";
        private float perfLogZaman = -100f;
        private void PerfBasla() { perfBas = perfSon = perfSaat.ElapsedTicks; perfEnCok = 0; perfEnAd = ""; }
        private void PerfIsaret(string ad)
        {
            long t = perfSaat.ElapsedTicks, d = t - perfSon;
            perfSon = t;
            if (d > perfEnCok) { perfEnCok = d; perfEnAd = ad; }
        }
        private void PerfBitir(string kare)
        {
            double top = (perfSaat.ElapsedTicks - perfBas) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (top < 6.0 || Time.time - perfLogZaman < 10f) return;
            perfLogZaman = Time.time;
            Logger.LogInfo("[PERF] slow " + kare + ": " + top.ToString("0.0") + " ms, slowest part: " + perfEnAd + " " + (perfEnCok * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0") + " ms");
        }

        void Update()
        {
            PerfBasla();
            if (tusDinle) { if (!menuAcik) tusDinle = false; else TusDinleKare(); }
            bool fBasildi = !tusDinle && Basildi(0, menuTus);
            if (fBasildi && !(menuAcik && aramaOdak)) MenuDegistir();   // menu tusu (arama kutusuna yazarken kapanmaz)
            // (H / J / P hata ayiklama tuslari kaldirildi: H butun sahneyi log'a yazip oyunu donduruyordu)
            try { AnimKayitKare(); } catch (Exception ex) { Logger.LogWarning("Anim record error: " + ex.Message); animKayit = null; }

            if (menuAcik)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (resimEkle) { resimEkle = false; DosyaPenceresiAc(); }
            DosyaPenceresiKontrol();

            PerfIsaret("menu/input");
            try { YerelKare(); } catch (Exception ex) { Logger.LogWarning("Local flag frame error: " + ex.Message); }
            PerfIsaret("local flag");
            try { YerelAyakKare(); } catch (Exception ex) { Logger.LogWarning("Base stand error: " + ex.Message); }

            try { YerelTirmanKare(); } catch (Exception ex) { Logger.LogWarning("Climb collider error: " + ex.Message); }
            try { YuvaDinleKare(); } catch (Exception) { }
            PerfIsaret("base/climb");
            try { EkranKare(); } catch (Exception ex) { Logger.LogWarning("Wall screen frame error: " + ex.Message); }
            PerfIsaret("wall screen");
            try { AgGuncelle(); UzakKare(); UzakAyakKare(); UzakTirmanKare(); } catch (Exception ex) { Logger.LogWarning("Flag sync frame error: " + ex.Message); }
            PerfIsaret("other players");
            try { TasimaKare(); } catch (Exception ex) { Logger.LogWarning("Flag carry frame error: " + ex.Message); }
            PerfIsaret("carry");
            try { GoreKare(); } catch (Exception ex) { Logger.LogWarning("Gorebox damage frame error: " + ex.Message); }

            if (Time.time - yerelZaman > (yerelKopya == null ? 0.25f : 1f))
            {
                yerelZaman = Time.time;
                try { YerelGuncelle(); } catch (Exception ex) { Logger.LogWarning("Local flag error: " + ex.Message); }
                PerfIsaret("local flag check (1 s)");
            }

            // kaydirirken her karede degil, en fazla saniyede ~8 kez (donmayi onler)
            if ((donusDirty && Time.time - donusZaman > 0.12f) || Time.time - donusZaman > 1.5f)
            {
                donusDirty = false;
                donusZaman = Time.time;
                DonusGuncelle();
                try { YerelDonusUygula(); } catch (Exception ex) { Logger.LogWarning("Flag rotation error: " + ex.Message); }
                if (donusCfg != null && donusCfg.Value != donus) donusCfg.Value = donus;
                PerfIsaret("rotation / size rebuild");
            }

            if (Time.time - detayZaman > 2f)
            {
                detayZaman = Time.time;
                try { GercekMeshUygula(); } catch (Exception ex) { Logger.LogWarning("Flag detail error: " + ex.Message); }
                PerfIsaret("flag detail (2 s)");
            }

            if (Etkin && Time.time - zamanlayici > 2f)
            {
                zamanlayici = Time.time;
                BayragiUygula();
                PerfIsaret("apply flag (2 s)");
            }
            PerfBitir("Update");
        }

        private void MenuDegistir()
        {
            menuAcik = !menuAcik;
            if (menuAcik)
            {
                eskiKilit = Cursor.lockState;
                eskiGorunur = Cursor.visible;
            }
            else
            {
                Cursor.lockState = eskiKilit;
                Cursor.visible = eskiGorunur;
            }
        }

        // ---------- Menu ----------

        private void Yenile()
        {
            SecimleriKaydet();
            if (Etkin) Sec(secili);
        }

        // ----- Temalar -----
        private struct Tema
        {
            public string ad;
            public Color panel, vurgu, yazi, soluk, acik;
            public bool aydinlik;
        }

        private static Color H(string hex)
        {
            int r = Convert.ToInt32(hex.Substring(1, 2), 16);
            int g = Convert.ToInt32(hex.Substring(3, 2), 16);
            int b = Convert.ToInt32(hex.Substring(5, 2), 16);
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }

        private static Tema T(string ad, string panel, string vurgu, string yazi, string soluk, string on, bool aydinlik)
        {
            Tema t = new Tema();
            t.ad = ad;
            t.panel = H(panel);
            t.panel.a = 0.96f;
            t.vurgu = H(vurgu);
            t.yazi = H(yazi);
            t.soluk = H(soluk);
            t.acik = H(on);
            t.aydinlik = aydinlik;
            return t;
        }

        private static readonly Tema[] Temalar = new Tema[]
        {
            T("Siyah", "#2b2b31", "#0d0d0f", "#ececf2", "#8b8b99", "#0d0d0f", false),
            T("Beyaz", "#16161a", "#f2f2f2", "#ececf2", "#8b8b99", "#f2f2f2", false),
            T("Gri", "#16161a", "#8e8e96", "#ececf2", "#8b8b99", "#8e8e96", false),
            T("Kirmizi", "#16161a", "#e53935", "#ececf2", "#8b8b99", "#e53935", false),
            T("Mavi", "#16161a", "#1e88e5", "#ececf2", "#8b8b99", "#1e88e5", false),
            T("Sari", "#16161a", "#fdd835", "#ececf2", "#8b8b99", "#fdd835", false),
            T("Yesil", "#16161a", "#43a047", "#ececf2", "#8b8b99", "#43a047", false),
            T("Turuncu", "#16161a", "#fb8c00", "#ececf2", "#8b8b99", "#fb8c00", false),
            T("Mor", "#16161a", "#8e24aa", "#ececf2", "#8b8b99", "#8e24aa", false),
            T("Kahverengi", "#16161a", "#8d5a3b", "#ececf2", "#8b8b99", "#8d5a3b", false),
            T("Pembe", "#16161a", "#ec407a", "#ececf2", "#8b8b99", "#ec407a", false),
            T("Lacivert", "#16161a", "#283593", "#ececf2", "#8b8b99", "#283593", false),
            T("Bej", "#16161a", "#d9c4a0", "#ececf2", "#8b8b99", "#d9c4a0", false),
            T("Turkuaz", "#16161a", "#1abc9c", "#ececf2", "#8b8b99", "#1abc9c", false),
        };

        private int temaNo = 3;   // varsayilan: Kirmizi
        private BepInEx.Configuration.ConfigEntry<int> temaCfg;
        private bool stilGecersiz;
        private readonly List<Texture2D> stilTexler = new List<Texture2D>();
        private readonly List<Texture2D> thumbs = new List<Texture2D>();

        private float panelX = 14f, panelY = 14f;
        private float panelH = 420f, icerikH = 300f;
        private bool surukle;
        private Vector2 surukleFark;
        private int sekme = 2;   // General ilk sekme

        private GUIStyle sPanel, sBos, sBaslik, sKat, sYazi, sBaslikSatir, sAltSatir, sOrta, sOrtaBeyaz, sOrtaSoluk, sOrtaKucuk, sOrtaSecili, sOrtaBuyuk;
        private GUIStyle sArama, sAramaYer, sChipOn, sSilYazi, sKaraPerde, sOrtaBeyazKucuk, sMiniKirmizi, sDevUD, sSagKucuk, sSilYazi13, sMade;
        private GUIStyle sTrack, sTabOn, sKutu, sKartSecili, sTogOn, sTogOff, sHover, sIkonKutu, sInce, sIncePolu;
        private Texture2D tDaire, dialTex;
        private int dialAci = -1;

        // rotasyon
        private class DKayit { public Mesh orijinal, donuk; public int aci; }
        private readonly Dictionary<Renderer, DKayit> donusKayit = new Dictionary<Renderer, DKayit>();
        private int donus = 0;
        private bool donusDirty;
        private float donusZaman;

        private Texture2D Yuvarlak(Color c, int r)
        {
            int n = r * 2 + 2;
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = Mathf.Max(Mathf.Max(r - x - 0.5f, x - (n - r) + 0.5f), 0f);
                    float dy = Mathf.Max(Mathf.Max(r - y - 0.5f, y - (n - r) + 0.5f), 0f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(r - d + 0.5f);
                    t.SetPixel(x, y, new Color(c.r, c.g, c.b, c.a * a));
                }
            }
            t.Apply();
            stilTexler.Add(t);
            return t;
        }

        private GUIStyle Kutu(Color c, int r)
        {
            GUIStyle s = new GUIStyle();
            s.normal.background = Yuvarlak(c, r);
            s.border = new RectOffset(r + 1, r + 1, r + 1, r + 1);
            return s;
        }

        private void StilTemizle()
        {
            foreach (Texture2D t in stilTexler)
                if (t != null) Destroy(t);
            stilTexler.Clear();
            if (dialTex != null) Destroy(dialTex);
            dialTex = null;
            dialAci = -1;
            sPanel = null;
        }

        private GUIStyle MetinStili(GUIStyle temel, int boyut, Color renk, bool orta)
        {
            GUIStyle s = new GUIStyle(temel);
            s.fontSize = boyut;
            s.normal.textColor = renk;
            s.hover.textColor = renk;
            s.active.textColor = renk;
            if (orta)
            {
                s.normal.background = null;
                s.hover.background = null;
                s.active.background = null;
            }
            return s;
        }

        private void StilHazirla()
        {
            if (sPanel != null) return;

            Tema th = Temalar[temaNo];
            Color kutuRenk = th.aydinlik ? new Color(0f, 0f, 0f, 0.06f) : new Color(1f, 1f, 1f, 0.06f);
            Color hoverRenk = th.aydinlik ? new Color(0f, 0f, 0f, 0.07f) : new Color(1f, 1f, 1f, 0.08f);
            Color kapaliRenk = th.aydinlik ? new Color(0.62f, 0.64f, 0.70f, 1f) : new Color(1f, 1f, 1f, 0.16f);
            Color beyaz = Color.white;
            Color tabYazi = VurguYazi(th.vurgu);   // acik renk vurguda koyu yazi

            tDaire = Yuvarlak(Color.white, 31);

            sPanel = Kutu(th.panel, 26);
            sBos = new GUIStyle();

            sBaslik = MetinStili(GUI.skin.label, 20, th.yazi, false);
            sKat = MetinStili(GUI.skin.label, 11, th.soluk, false);
            sYazi = MetinStili(GUI.skin.label, 14, th.yazi, false);
            sYazi.padding = new RectOffset(0, 0, 8, 0);
            sBaslikSatir = MetinStili(GUI.skin.label, 14, th.yazi, false);
            sAltSatir = MetinStili(GUI.skin.label, 11, th.soluk, false);

            sOrta = MetinStili(GUI.skin.button, 13, th.yazi, true);
            sOrtaBeyaz = MetinStili(GUI.skin.button, 13, tabYazi, true);
            sOrtaSoluk = MetinStili(GUI.skin.button, 13, th.soluk, true);
            sOrtaKucuk = MetinStili(GUI.skin.button, 11, th.soluk, true);
            sOrtaSecili = MetinStili(GUI.skin.button, 11, th.yazi, true);
            sOrtaBuyuk = MetinStili(GUI.skin.button, 17, th.yazi, true);

            sTrack = Kutu(kutuRenk, 16);
            sTabOn = Kutu(th.vurgu, 14);
            sKutu = Kutu(kutuRenk, 14);
            sKartSecili = Kutu(th.vurgu, 12);
            sTogOn = Kutu(th.vurgu, 11);
            sTogOff = Kutu(kapaliRenk, 11);
            sIkonKutu = Kutu(new Color(th.vurgu.r, th.vurgu.g, th.vurgu.b, 0.18f), 10);
            sInce = Kutu(kapaliRenk, 3);
            sIncePolu = Kutu(th.vurgu, 3);

            sChipOn = Kutu(new Color(th.vurgu.r, th.vurgu.g, th.vurgu.b, 0.28f), 14);
            sSilYazi = MetinStili(GUI.skin.button, 11, th.vurgu, true);
            sSilYazi13 = MetinStili(GUI.skin.button, 13, th.vurgu, true);
            sKaraPerde = Kutu(new Color(0f, 0f, 0f, 0.6f), 12);
            sMiniKirmizi = Kutu(th.vurgu, 8);
            sDevUD = MetinStili(GUI.skin.label, 13, th.vurgu, false);
            sDevUD.wordWrap = false;
            sDevUD.padding = new RectOffset(0, 0, 0, 0);
            sSagKucuk = MetinStili(GUI.skin.button, 11, th.soluk, true);
            sMade = MetinStili(GUI.skin.label, 13, th.soluk, false);
            sMade.padding = new RectOffset(0, 0, 0, 0);
            sMade.wordWrap = false;
            sOrtaBeyazKucuk = MetinStili(GUI.skin.button, 11, Color.white, true);
            sOrtaBeyazKucuk.padding = new RectOffset(0, 0, 0, 2);

            sArama = new GUIStyle(GUI.skin.textField);
            Texture2D aramaBg = sKutu.normal.background;
            sArama.normal.background = aramaBg;
            sArama.hover.background = aramaBg;
            sArama.focused.background = aramaBg;
            sArama.active.background = aramaBg;
            sArama.onNormal.background = aramaBg;
            sArama.onFocused.background = aramaBg;
            sArama.border = new RectOffset(15, 15, 15, 15);
            sArama.fontSize = 13;
            sArama.normal.textColor = th.yazi;
            sArama.hover.textColor = th.yazi;
            sArama.focused.textColor = th.yazi;
            sArama.active.textColor = th.yazi;
            sArama.padding = new RectOffset(14, 12, 11, 10);
            sAramaYer = MetinStili(GUI.skin.label, 13, th.soluk, false);
            sAramaYer.padding = new RectOffset(0, 0, 10, 0);

            sHover = new GUIStyle();
            sHover.hover.background = Yuvarlak(hoverRenk, 12);
            sHover.active.background = Yuvarlak(hoverRenk, 12);
            sHover.border = new RectOffset(13, 13, 13, 13);
        }

        // ---------- kucuk bilesenler ----------

        private bool Tikla(Rect r)
        {
            return GUI.Button(r, GUIContent.none, sHover);
        }

        // menu kapaliyken (GUI.enabled false) secili kutular da soluk gorunsun
        private void SecKutu(Rect r, GUIStyle st)
        {
            Color e = GUI.color;
            if (!GUI.enabled && st == sTabOn) GUI.color = new Color(e.r, e.g, e.b, e.a * 0.35f);
            GUI.Box(r, GUIContent.none, st);
            GUI.color = e;
        }

        private void EtiketYaz(string t, float cy, float iw)
        {
            GUI.Label(new Rect(4, cy, iw, 18), t, sKat);
        }

        private bool Anahtar(float cy, float iw, string yazi, bool deger)
        {
            Rect r = new Rect(0, cy, iw, 36);
            bool tik = Tikla(r);
            GUI.Label(new Rect(r.x + 6, r.y, r.width - 60, r.height), yazi, sYazi);

            Rect tk = new Rect(r.xMax - 48, r.y + 7, 42, 22);
            GUI.Box(tk, GUIContent.none, deger ? sTogOn : sTogOff);
            Color eski = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(deger ? tk.xMax - 20 : tk.x + 4, tk.y + 3, 16, 16), tDaire);
            GUI.color = eski;
            return tik;
        }

        private int Kaydirici(Rect r, int deger, int min, int max)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event e = Event.current;
            float frac = Mathf.InverseLerp(min, max, deger);

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (GUI.enabled && e.button == 0 && r.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        deger = Mathf.RoundToInt(Mathf.Lerp(min, max, Mathf.Clamp01((e.mousePosition.x - r.x) / r.width)));
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        deger = Mathf.RoundToInt(Mathf.Lerp(min, max, Mathf.Clamp01((e.mousePosition.x - r.x) / r.width)));
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
                case EventType.Repaint:
                    Rect tr = new Rect(r.x, r.center.y - 3, r.width, 6);
                    GUI.Box(tr, GUIContent.none, sInce);
                    float dolu = r.width * frac;
                    if (dolu >= 8f) GUI.Box(new Rect(tr.x, tr.y, dolu, 6), GUIContent.none, sIncePolu);
                    Color eski = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, eski.a);   // menu kapaliyken soluk kalsin
                    GUI.DrawTexture(new Rect(r.x + dolu - 8, r.center.y - 8, 16, 16), tDaire);
                    GUI.color = eski;
                    break;
            }

            return Mathf.Clamp(deger, min, max);
        }

        private void DialGuncelle(int deg)
        {
            const int N = 74;
            if (dialTex == null)
            {
                dialTex = new Texture2D(N, N, TextureFormat.RGBA32, false);
                dialTex.hideFlags = HideFlags.HideAndDontSave;
                dialTex.filterMode = FilterMode.Bilinear;
                dialTex.wrapMode = TextureWrapMode.Clamp;
                dialAci = -1;
            }
            if (dialAci == deg) return;
            dialAci = deg;

            Tema th = Temalar[temaNo];
            Color acik = th.vurgu;
            Color kapali = th.aydinlik ? new Color(0.62f, 0.64f, 0.70f, 1f) : new Color(1f, 1f, 1f, 0.16f);
            float c = (N - 1) * 0.5f;

            Color32[] px = new Color32[N * N];
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float dx = x - c, dy = y - c;
                    float rad = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(Mathf.Min(rad - 28.5f, 36.5f - rad) + 0.5f);
                    float ang = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
                    if (ang < 0f) ang += 360f;
                    Color col = (deg >= 360 || ang <= deg) ? acik : kapali;
                    px[y * N + x] = new Color32((byte)(col.r * 255f), (byte)(col.g * 255f), (byte)(col.b * 255f), (byte)(col.a * a * 255f));
                }
            }
            dialTex.SetPixels32(px);
            dialTex.Apply();
        }

        private void ThumbHazirla()
        {
            foreach (Texture2D t in thumbs)
                if (t != null) Destroy(t);
            thumbs.Clear();

            const int W = 176, Hh = 132, R = 16;
            for (int i = 0; i < bayraklar.Count; i++)
            {
                try
                {
                    Texture2D k = Kucult(bayraklar[i], W, Hh);
                    Color32[] px = k.GetPixels32();
                    for (int y = 0; y < Hh; y++)
                    {
                        for (int x = 0; x < W; x++)
                        {
                            float dx = Mathf.Max(Mathf.Max(R - x - 0.5f, x - (W - R) + 0.5f), 0f);
                            float dy = Mathf.Max(Mathf.Max(R - y - 0.5f, y - (Hh - R) + 0.5f), 0f);
                            float a = Mathf.Clamp01(R - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                            Color32 p = px[y * W + x];
                            p.a = (byte)(a * 255f);
                            px[y * W + x] = p;
                        }
                    }
                    k.SetPixels32(px);
                    k.Apply();
                    k.filterMode = FilterMode.Bilinear;
                    k.wrapMode = TextureWrapMode.Clamp;
                    thumbs.Add(k);
                }
                catch (Exception e)
                {
                    Logger.LogWarning("Thumbnail error: " + e.Message);
                    thumbs.Add(null);
                }
            }
        }

        // ---------- Dil ----------
        private static readonly string[] DilAdlari = { "English", "Türkçe", "Español", "Русский" };
        private int dil = 0;
        private BepInEx.Configuration.ConfigEntry<int> dilCfg;

        private static readonly Dictionary<string, string[]> Ceviri = new Dictionary<string, string[]>
        {
            { "APPEARANCE", new string[] { "GÖRÜNÜM", "APARIENCIA", "ВНЕШНИЙ ВИД" } },
            { "COLOR", new string[] { "RENK", "COLOR", "ЦВЕТ" } },
            { "FONT", new string[] { "YAZI TİPİ", "FUENTE", "ШРИФТ" } },
            { "Dark", new string[] { "Koyu", "Oscuro", "Тёмная" } },
            { "Light", new string[] { "Açık", "Claro", "Светлая" } },
            { "Gorilla Tag", new string[] { "Gorilla Tag", "Gorilla Tag", "Gorilla Tag" } },
            { "Modern", new string[] { "Modern", "Moderna", "Современный" } },
            { "Click again to reset", new string[] { "Sıfırlamak için tekrar bas", "Pulsa otra vez para restablecer", "Нажмите ещё раз для сброса" } },
            { "Delete selected", new string[] { "Seçileni sil", "Borrar seleccionada", "Удалить выбранный" } },
            { "Page", new string[] { "Sayfa", "Página", "Страница" } },
            { "More", new string[] { "Diğer", "Más", "Ещё" } },
            { "Plant button 1", new string[] { "Saplama tuşu 1", "Botón de clavar 1", "Кнопка установки 1" } },
            { "Plant button 2", new string[] { "Saplama tuşu 2", "Botón de clavar 2", "Кнопка установки 2" } },
            { "Press a button...", new string[] { "Bir tuşa bas...", "Pulsa un botón...", "Нажмите кнопку..." } },
            { "Click a box, then press the new key or controller button. Click again to clear.", new string[] { "Bir kutuya tıkla, sonra yeni tuşa ya da kumanda düğmesine bas. Temizlemek için tekrar tıkla.", "Pulsa una casilla y luego la nueva tecla o botón del mando. Pulsa otra vez para borrar.", "Нажмите на поле, затем новую клавишу или кнопку контроллера. Нажмите ещё раз, чтобы очистить." } },
            { "PLANT STRENGTH", new string[] { "SAPLAMA GÜCÜ", "FUERZA PARA CLAVAR", "СИЛА УДАРА" } },
            { "Softest", new string[] { "En hafif", "Muy suave", "Очень мягко" } },
            { "Soft", new string[] { "Hafif", "Suave", "Мягко" } },
            { "Hard", new string[] { "Sert", "Fuerte", "Сильно" } },
            { "Hardest", new string[] { "En sert", "Muy fuerte", "Очень сильно" } },
            { "How hard you must hit the ground to plant the flag. Normal is the original.", new string[] { "Bayrağı saplamak için yere ne kadar sert vurman gerektiği. Normal, orijinal ayardır.", "Qué tan fuerte debes golpear el suelo para clavar la bandera. Normal es el original.", "Насколько сильно нужно ударить о землю, чтобы воткнуть флаг. Обычно — исходная настройка." } },
            { "SHARING", new string[] { "PAYLAŞIM", "COMPARTIR", "ОБЩИЙ ДОСТУП" } },
            { "Others can take my flag", new string[] { "Başkaları bayrağımı alabilir", "Otros pueden tomar mi bandera", "Другие могут брать мой флаг" } },
            { "In private rooms, players with this mod can pick up your planted flag, carry it, drop it or plant it again.", new string[] { "Özel odalarda, bu modu olan oyuncular sapladığın bayrağı alıp taşıyabilir, bırakabilir ya da tekrar saplayabilir.", "En salas privadas, los jugadores con este mod pueden tomar tu bandera clavada, llevarla, soltarla o volver a clavarla.", "В приватных комнатах игроки с этим модом могут поднять ваш воткнутый флаг, нести его, бросить или снова воткнуть." } },
            { "Mod users can carry, drop and replant your planted flag.", new string[] { "Modu olanlar sapladığın bayrağı taşıyıp bırakabilir, yeniden saplayabilir.", "Los usuarios del mod pueden llevar, soltar y volver a clavar tu bandera.", "Игроки с модом могут нести, бросать и снова втыкать ваш флаг." } },
            { "RETURN TO BACK AFTER", new string[] { "SIRTA DÖNME MESAFESİ", "VUELVE A LA ESPALDA TRAS", "ВОЗВРАТ НА СПИНУ ПОСЛЕ" } },
            { "Never", new string[] { "Asla", "Nunca", "Никогда" } },
            { "The flag returns to your back past this distance.", new string[] { "Bu mesafeyi geçince bayrak sırtına döner.", "La bandera vuelve a tu espalda pasada esta distancia.", "Флаг вернётся на спину дальше этого расстояния." } },
            { "Your planted or dropped flag goes back to your back when you get this far from it.", new string[] { "Saplı ya da yere düşmüş bayrağından bu kadar uzaklaşınca bayrak sırtına döner.", "Tu bandera clavada o caída vuelve a tu espalda cuando te alejas esta distancia.", "Воткнутый или брошенный флаг вернётся на спину, когда вы отойдёте на это расстояние." } },
            { "Selected. Click to pick a new image", new string[] { "Seçili. Yeni resim seçmek için tıkla", "Seleccionado. Pulsa para elegir otra imagen", "Выбрано. Нажмите, чтобы выбрать новое изображение" } },
            { "Use your own image for the pole", new string[] { "Sap için kendi resmini kullan", "Usa tu propia imagen para el mástil", "Своё изображение для древка" } },
            { "Flags", new string[] { "Bayraklar", "Banderas", "Флаги" } },
            { "Style", new string[] { "Stil", "Estilo", "Стиль" } },
            { "General", new string[] { "Genel", "General", "Общие" } },
            { "WOOD", new string[] { "AHŞAP", "MADERA", "ДЕРЕВО" } },
            { "METAL", new string[] { "METAL", "METAL", "МЕТАЛЛ" } },
            { "COLORS", new string[] { "RENKLER", "COLORES", "ЦВЕТА" } },
            { "PATTERNS", new string[] { "DESENLER", "PATRONES", "УЗОРЫ" } },
            { "Original", new string[] { "Orijinal", "Original", "Оригинал" } },
            { "Light wood", new string[] { "Açık ahşap", "Madera clara", "Светлое" } },
            { "Walnut", new string[] { "Ceviz", "Nogal", "Орех" } },
            { "Mahogany", new string[] { "Maun", "Caoba", "Махагони" } },
            { "Silver", new string[] { "Gümüş", "Plata", "Серебро" } },
            { "Gold", new string[] { "Altın", "Oro", "Золото" } },
            { "Bronze", new string[] { "Bronz", "Bronce", "Бронза" } },
            { "Black metal", new string[] { "Siyah metal", "Metal negro", "Чёрный металл" } },
            { "White", new string[] { "Beyaz", "Blanco", "Белый" } },
            { "Black", new string[] { "Siyah", "Negro", "Чёрный" } },
            { "Red", new string[] { "Kırmızı", "Rojo", "Красный" } },
            { "Blue", new string[] { "Mavi", "Azul", "Синий" } },
            { "Candy cane", new string[] { "Şeker kamışı", "Bastón", "Леденец" } },
            { "Rainbow", new string[] { "Gökkuşağı", "Arcoíris", "Радуга" } },
            { "Two-tone", new string[] { "İki renk", "Dos tonos", "Два цвета" } },
            { "Custom PNG", new string[] { "Kendi PNG'n", "PNG propio", "Свой PNG" } },
            { "Birch", new string[] { "Huş", "Abedul", "Берёза" } },
            { "Ebony", new string[] { "Abanoz", "Ébano", "Эбен" } },
            { "Bamboo", new string[] { "Bambu", "Bambú", "Бамбук" } },
            { "Pine", new string[] { "Çam", "Pino", "Сосна" } },
            { "Copper", new string[] { "Bakır", "Cobre", "Медь" } },
            { "Rose gold", new string[] { "Rose altın", "Oro rosa", "Розовое золото" } },
            { "Titanium", new string[] { "Titanyum", "Titanio", "Титан" } },
            { "Rusty iron", new string[] { "Paslı demir", "Hierro oxidado", "Ржавое железо" } },
            { "Green", new string[] { "Yeşil", "Verde", "Зелёный" } },
            { "Yellow", new string[] { "Sarı", "Amarillo", "Жёлтый" } },
            { "Orange", new string[] { "Turuncu", "Naranja", "Оранжевый" } },
            { "Pink", new string[] { "Pembe", "Rosa", "Розовый" } },
            { "Purple", new string[] { "Mor", "Morado", "Фиолетовый" } },
            { "Cyan", new string[] { "Turkuaz", "Cian", "Голубой" } },
            { "Checkered", new string[] { "Damalı", "Cuadros", "Клетка" } },
            { "Zebra", new string[] { "Zebra", "Cebra", "Зебра" } },
            { "Camo", new string[] { "Kamuflaj", "Camuflaje", "Камуфляж" } },
            { "Sunset", new string[] { "Gün batımı", "Atardecer", "Закат" } },
            { "Galaxy", new string[] { "Galaksi", "Galaxia", "Галактика" } },
            { "Blue spiral", new string[] { "Mavi spiral", "Espiral azul", "Синяя спираль" } },
            { "SIDE", new string[] { "TARAF", "LADO", "СТОРОНА" } },
            { "Turn on Private in Variant to choose a side.", new string[] { "Taraf seçmek için Değişken'i Gizli yap.", "Activa Privado en Variante para elegir lado.", "Включите приватный вариант, чтобы выбрать сторону." } },
            { "The real flag is on: change the shoulder from the wardrobe in game.", new string[] { "Gerçek bayrak takılı: omzu oyundaki gardıroptan değiştir.", "La bandera real está puesta: cambia el hombro en el armario del juego.", "Надет настоящий флаг: плечо меняется в гардеробе игры." } },
            { "Left", new string[] { "Sol", "Izquierda", "Слева" } },
            { "Right", new string[] { "Sağ", "Derecha", "Справа" } },
            { "Which shoulder the flag sits on. Only for Private.", new string[] { "Bayrağın hangi omzunda duracağı. Sadece Gizli modda.", "En qué hombro va la bandera. Solo en Privado.", "На каком плече флаг. Только для приватного режима." } },
            { "FLAG DETAIL", new string[] { "BAYRAK DETAYI", "DETALLE DE BANDERA", "ДЕТАЛИЗАЦИЯ ФЛАГА" } },
            { "TEXTURE QUALITY", new string[] { "DOKU KALİTESİ", "CALIDAD DE TEXTURA", "КАЧЕСТВО ТЕКСТУРЫ" } },
            { "Normal", new string[] { "Normal", "Normal", "Обычное" } },
            { "High", new string[] { "Yüksek", "Alta", "Высокое" } },
            { "Ultra", new string[] { "Ultra", "Ultra", "Ультра" } },
            { "Sharper flag image. Normal is the game's original quality.", new string[] { "Bayrak resmi daha net olur. Normal, oyunun orijinal kalitesi.", "Imagen de bandera más nítida. Normal es la calidad original.", "Более чёткий флаг. Обычное — исходное качество игры." } },
            { "More parts = smoother waves. Only you see the difference.", new string[] { "Daha çok parça = daha yumuşak dalga. Farkı sadece sen görürsün.", "Más partes = ondas más suaves. Solo tú ves la diferencia.", "Больше частей = плавнее волны. Разницу видите только вы." } },
            { "Delete custom pole", new string[] { "Kendi sapını sil", "Borrar mástil propio", "Удалить своё древко" } },
            { "Removes the image you added for the pole", new string[] { "Sap için eklediğin resmi siler", "Borra la imagen que añadiste", "Удаляет добавленную картинку" } },
            { "Changes the color of the flag pole. Only the pole changes, the flag stays the same.", new string[] { "Bayrak sapının rengini değiştirir. Sadece sap değişir, bayrak aynı kalır.", "Cambia el color del mástil. Solo cambia el mástil, la bandera sigue igual.", "Меняет цвет древка. Меняется только древко, флаг остаётся прежним." } },
            { "Original pole", new string[] { "Orijinal sap", "Mástil original", "Исходное древко" } },
            { "Back to the default wood pole", new string[] { "Varsayılan ahşap sapa döner", "Vuelve al mástil de madera", "Вернуть деревянное древко" } },
            { "Pole", new string[] { "Sap", "Mástil", "Древко" } },
            { "Rotate", new string[] { "Döndür", "Rotar", "Поворот" } },
            { "Settings", new string[] { "Ayarlar", "Ajustes", "Настройки" } },
            { "F to close", new string[] { "F: kapat", "F: cerrar", "F: закрыть" } },
            { "{0} to close", new string[] { "{0}: kapat", "{0}: cerrar", "{0}: закрыть" } },
            { "KEYBINDS", new string[] { "TUŞ ATAMALARI", "CONTROLES", "КЛАВИШИ" } },
            { "PLANT FLAG", new string[] { "BAYRAK SAPLAMA", "CLAVAR BANDERA", "УСТАНОВКА ФЛАГА" } },
            { "Hold the plant button and hit the ground or a wall with the pole. Only you see it.", new string[] { "Saplama tuşuna basılı tutup sapı yere ya da duvara vur. Sadece sen görürsün.", "Mantén el botón y golpea el suelo o una pared con el mástil. Solo tú lo ves.", "Удерживайте кнопку и ударьте древком о землю или стену. Видите только вы." } },
            { "Open / close menu", new string[] { "Menüyü aç / kapat", "Abrir / cerrar menú", "Открыть / закрыть меню" } },
            { "Plant flag", new string[] { "Bayrağı sapla", "Clavar bandera", "Воткнуть флаг" } },
            { "Press a key...", new string[] { "Bir tuşa bas...", "Pulsa una tecla...", "Нажмите клавишу..." } },
            { "Click a box to change it. For the menu key, press the new key (Esc cancels).", new string[] { "Değiştirmek için kutuya tıkla. Menü tuşu için yeni tuşa bas (Esc iptal eder).", "Haz clic en un cuadro para cambiarlo. Para el menú, pulsa la nueva tecla (Esc cancela).", "Нажмите на поле, чтобы изменить. Для меню нажмите новую клавишу (Esc — отмена)." } },
            { "Countries", new string[] { "Ülkeler", "Países", "Страны" } },
            { "Pride", new string[] { "Onur", "Orgullo", "Прайд" } },
            { "Regions", new string[] { "Bölgeler", "Regiones", "Регионы" } },
            { "Territories", new string[] { "Bölgeler", "Territorios", "Территории" } },
            { "Custom", new string[] { "Özel", "Propias", "Свои" } },
            { "Search flags...", new string[] { "Bayrak ara...", "Buscar banderas...", "Поиск флагов..." } },
            { "RESULTS", new string[] { "SONUÇLAR", "RESULTADOS", "РЕЗУЛЬТАТЫ" } },
            { "Images you add with \"Add image\" show up here.", new string[] { "\"Resim ekle\" ile eklediğin resimler burada.", "Aquí aparecen las imágenes que añadas.", "Здесь появятся добавленные картинки." } },
            { "No flags found.", new string[] { "Bayrak bulunamadı.", "No se encontraron banderas.", "Флаги не найдены." } },
            { "Add image", new string[] { "Resim ekle", "Añadir imagen", "Добавить" } },
            { "+ Add image", new string[] { "+ Resim ekle", "+ Añadir imagen", "+ Добавить" } },
            { "Original flag", new string[] { "Orijinale dön", "Bandera original", "Исходный флаг" } },
            { "THEME", new string[] { "TEMA", "TEMA", "ТЕМА" } },
            { "LANGUAGE", new string[] { "DİL", "IDIOMA", "ЯЗЫК" } },
            { "FLAG SIDES", new string[] { "BAYRAK YÜZLERİ", "CARAS DE LA BANDERA", "СТОРОНЫ ФЛАГА" } },
            { "Side 1: mirror", new string[] { "1. yüz: ayna çevir", "Cara 1: espejo", "Сторона 1: зеркально" } },
            { "Side 1: upside down", new string[] { "1. yüz: baş aşağı", "Cara 1: al revés", "Сторона 1: вверх ногами" } },
            { "Side 2: mirror", new string[] { "2. yüz: ayna çevir", "Cara 2: espejo", "Сторона 2: зеркально" } },
            { "Side 2: upside down", new string[] { "2. yüz: baş aşağı", "Cara 2: al revés", "Сторона 2: вверх ногами" } },
            { "ROTATION", new string[] { "DÖNÜŞ", "ROTACIÓN", "ПОВОРОТ" } },
            { "Turn the flag around its pole", new string[] { "Bayrağı sapı etrafında döndür", "Gira la bandera sobre el mástil", "Поворот флага вокруг древка" } },
            { "QUICK ANGLES", new string[] { "HAZIR AÇILAR", "ÁNGULOS RÁPIDOS", "БЫСТРЫЕ УГЛЫ" } },
            { "APPLY METHOD", new string[] { "UYGULAMA YÖNTEMİ", "MÉTODO", "МЕТОД ПРИМЕНЕНИЯ" } },
            { "Keep it on Array. Only change it if the flag looks broken.", new string[] { "Array'de bırak. Sadece bayrak bozuk görünürse değiştir.", "Déjalo en Array. Cámbialo solo si la bandera se ve rota.", "Оставьте Array. Меняйте, только если флаг сломан." } },
            { "ACTIONS", new string[] { "İŞLEMLER", "ACCIONES", "ДЕЙСТВИЯ" } },
            { "Reload flags", new string[] { "Bayrakları yenile", "Recargar banderas", "Обновить флаги" } },
            { "Use after putting PNGs in the Flags folder yourself", new string[] { "Flags klasörüne kendin PNG koyduysan bas", "Úsalo si pusiste PNG en la carpeta Flags", "Если сами добавили PNG в папку Flags" } },
            { "Save flag model (debug)", new string[] { "Bayrak modelini kaydet (debug)", "Guardar modelo (debug)", "Сохранить модель (debug)" } },
            { "Only for bug reports, you don't need it", new string[] { "Sadece hata bildirmek için, gerekmez", "Solo para reportar errores", "Только для отчётов об ошибках" } },
            { "Reset all settings", new string[] { "Tüm ayarları sıfırla", "Restablecer todo", "Сбросить все настройки" } },
            { "Everything back to default", new string[] { "Her şey varsayılana döner", "Todo vuelve a lo predeterminado", "Всё по умолчанию" } },
            { " flags loaded", new string[] { " bayrak yüklü", " banderas cargadas", " флагов загружено" } },
            { "Choose an image in the window...", new string[] { "Açılan pencereden bir resim seç...", "Elige una imagen en la ventana...", "Выберите картинку в окне..." } },
            { "Could not open file window: ", new string[] { "Dosya penceresi açılamadı: ", "No se pudo abrir la ventana: ", "Не удалось открыть окно: " } },
            { "Added: ", new string[] { "Eklendi: ", "Añadido: ", "Добавлено: " } },
            { "Could not add image: ", new string[] { "Resim eklenemedi: ", "No se pudo añadir la imagen: ", "Не удалось добавить картинку: " } },
            { "Rotation: pole part not found in mesh.", new string[] { "Dönüş: modelde sap bulunamadı.", "Rotación: no se encontró el mástil.", "Поворот: древко не найдено." } },
            { "Rotation failed (mesh not readable?). See log.", new string[] { "Dönüş başarısız. Log'a bak.", "La rotación falló. Mira el log.", "Поворот не удался. См. лог." } },
            { "Flag not found. Equip the flag cosmetic, it applies automatically.", new string[] { "Bayrak bulunamadı. Bayrak kozmetiğini tak, otomatik uygulanır.", "Bandera no encontrada. Equipa el cosmético de bandera y se aplicará solo.", "Флаг не найден. Наденьте косметику флага, он применится сам." } },
            { "Flag not found. Equip it first.", new string[] { "Bayrak bulunamadı. Önce tak.", "Bandera no encontrada. Equípala primero.", "Флаг не найден. Сначала наденьте его." } },
            { "No mesh.", new string[] { "Mesh yok.", "Sin malla.", "Нет меша." } },
            { "Mesh dumped: plugins/debug/", new string[] { "Mesh kaydedildi: plugins/debug/", "Malla guardada: plugins/debug/", "Меш сохранён: plugins/debug/" } },
            { "Category name...", new string[] { "Kategori adı...", "Nombre de categoría...", "Название категории..." } },
            { "Create", new string[] { "Oluştur", "Crear", "Создать" } },
            { "Delete category", new string[] { "Kategoriyi sil", "Borrar categoría", "Удалить категорию" } },
            { "Click again to delete", new string[] { "Silmek için tekrar tıkla", "Pulsa otra vez para borrar", "Нажмите ещё раз" } },
            { "Delete?", new string[] { "Silinsin mi?", "¿Borrar?", "Удалить?" } },
            { "Select flags", new string[] { "Bayrak seç", "Elegir banderas", "Выбор флагов" } },
            { "Save", new string[] { "Kaydet", "Guardar", "Сохранить" } },
            { "Cancel", new string[] { "İptal", "Cancelar", "Отмена" } },
            { "Edit", new string[] { "Düzenle", "Editar", "Изменить" } },
            { "Create your own category first (+).", new string[] { "Önce kendi kategorini oluştur (+).", "Primero crea tu categoría (+).", "Сначала создайте категорию (+)." } },
            { "Add flags with Edit, or your own images with \"Add image\".", new string[] { "Düzenle ile bayrak, \"Resim ekle\" ile kendi resmini ekle.", "Añade banderas con Editar o imágenes propias.", "Добавляйте флаги через «Изменить» или свои картинки." } },
            { "VARIANT", new string[] { "DEĞİŞKEN", "VARIANTE", "ВАРИАНТ" } },
            { "None", new string[] { "Kapalı", "Ninguno", "Выкл" } },
            { "No flag is changed. The mod is off.", new string[] { "Hiçbir bayrak değişmez. Mod kapalı.", "No cambia ninguna bandera. El mod está apagado.", "Флаги не меняются. Мод выключен." } },
            { "The real flag cosmetics in game are not changed.", new string[] { "Oyundaki gerçek bayrak kozmetikleri değişmez.", "Los cosméticos de bandera reales no cambian.", "Настоящие косметические флаги не меняются." } },
            { "Private and Variant are off. Turn one on in General.", new string[] { "Gizli ve Değişken kapalı. General'den birini aç.", "Privado y Variante están apagados. Activa uno en General.", "Приватный и вариант выключены. Включите один в General." } },
            { "Turn on Private at the top to choose a side.", new string[] { "Taraf seçmek için en üstten Gizli'yi aç.", "Activa Privado arriba para elegir lado.", "Включите «Приватный» сверху, чтобы выбрать сторону." } },
            { "Flag pole options coming soon.", new string[] { "Bayrak sapı ayarları yakında.", "Opciones del mástil próximamente.", "Настройки древка скоро появятся." } },
            { "Pride Flag", new string[] { "LGBT Bayrağı (Pride Flag)", "Bandera Pride", "Флаг прайда" } },
            { "Trans Pride Flag", new string[] { "Trans Bayrağı (Trans Pride Flag)", "Bandera Trans", "Транс-флаг" } },
            { "Private", new string[] { "Gizli", "Privado", "Приватный" } },
            { "PRIVATE", new string[] { "GİZLİ", "PRIVADO", "ПРИВАТНЫЙ" } },
            { "On", new string[] { "Açık", "Activado", "Вкл" } },
            { "FLAG SETTINGS", new string[] { "BAYRAK AYARI", "AJUSTES DE BANDERA", "НАСТРОЙКИ ФЛАГА" } },
            { "Pole length", new string[] { "Sap uzunluğu", "Largo del mástil", "Длина древка" } },
            { "Flag size", new string[] { "Bayrak boyutu", "Tamaño de bandera", "Размер флага" } },
            { "Up to the pole length", new string[] { "En fazla sap boyu kadar", "Hasta el largo del mástil", "Не больше длины древка" } },
            { "Balance base", new string[] { "Denge ayağı", "Base de apoyo", "Подставка" } },
            { "Reset", new string[] { "Sıfırla", "Restablecer", "Сброс" } },
            { "Done", new string[] { "Tamam", "Listo", "Готово" } },
            { "Players", new string[] { "Oyuncular", "Jugadores", "Игроки" } },
            { "Plant flag is off. Pick a button to turn it on.", new string[] { "Saplama kapalı. Tuş seçince açılır.", "Clavar está apagado. Elige un botón para activarlo.", "Втыкание выключено. Выбери кнопку, чтобы включить." } },
            { "Organizations", new string[] { "Kuruluşlar", "Organizaciones", "Организации" } },
            { "Favorites", new string[] { "Favoriler", "Favoritas", "Избранное" } },
            { "Tap the star on a flag to add it here.", new string[] { "Buraya eklemek için bayraktaki yıldıza bas.", "Toca la estrella de una bandera para añadirla aquí.", "Нажми звезду на флаге, чтобы добавить его сюда." } },
            { "Wave", new string[] { "Dalgalanma", "Ondeo", "Развевание" } },
            { "Wind", new string[] { "Rüzgar", "Viento", "Ветер" } },
            { "Gentle", new string[] { "Hafif", "Suave", "Слабый" } },
            { "Medium", new string[] { "Orta", "Medio", "Средний" } },
            { "Strong", new string[] { "Sert", "Fuerte", "Сильный" } },
            { "Upright", new string[] { "Dimdik", "Recto", "Прямо" } },
            { "The pole stands straight up when planted.", new string[] { "Saplayınca sap dimdik durur.", "Al clavarla, el mástil queda recto.", "Воткнутое древко стоит прямо." } },
            { "Lock", new string[] { "Kilitle", "Bloquear", "Замок" } },
            { "Nobody can pick it up, not even you.", new string[] { "Kimse alamaz, sen de alamazsın.", "Nadie puede tomarla, ni tú.", "Никто не может взять, даже ты." } },
            { "THROW", new string[] { "FIRLATMA", "LANZAR", "БРОСОК" } },
            { "Throw & catch flag", new string[] { "Bayrağı fırlat ve yakala", "Lanzar y atrapar", "Бросать и ловить флаг" } },
            { "Swing and let go to throw the flag, grab it in the air to catch.", new string[] { "Sallayıp bırakınca fırlar, havada tutunca yakalarsın.", "Balancea y suelta para lanzar; agárrala en el aire.", "Взмахни и отпусти, чтобы бросить; хватай в воздухе." } },
            { "Pole thickness", new string[] { "Sap kalınlığı", "Grosor del mástil", "Толщина древка" } },
            { "Pole button", new string[] { "Sap butonu", "Botón del mástil", "Кнопка на древке" } },
            { "Small button on the pole that opens GF Sandbox (pole, flag size, base).", new string[] { "Sapta GF Sandbox'ı açan küçük buton (sap, bayrak boyu, ayak).", "Botón en el mástil que abre GF Sandbox (mástil, tamaño, base).", "Кнопка на древке, открывает GF Sandbox (древко, размер, подставка)." } },
            { "CLIMB", new string[] { "TIRMANMA", "ESCALAR", "ЛАЗАНИЕ" } },
            { "Climbable flag", new string[] { "Tırmanılabilir bayrak", "Bandera escalable", "Можно лазить по флагу" } },
            { "In modded lobbies planted flags are solid: you can climb them.", new string[] { "Modlu lobilerde saplı bayraklar katı olur, tırmanabilirsin.", "En salas con mods las banderas clavadas son sólidas: puedes escalarlas.", "В модовых лобби воткнутые флаги твёрдые: по ним можно лазить." } },
            { "MOD USERS", new string[] { "MOD KULLANANLAR", "USUARIOS DEL MOD", "ИГРОКИ С МОДОМ" } },
            { "Mod users", new string[] { "Mod kullananlar", "Usuarios del mod", "Игроки с модом" } },
            { "you", new string[] { "sen", "tú", "ты" } },
            { "On back", new string[] { "Sırtta", "En la espalda", "На спине" } },
            { "In hand", new string[] { "Elde", "En la mano", "В руке" } },
            { "Planted", new string[] { "Saplı", "Clavada", "Воткнут" } },
            { "Dropped", new string[] { "Yerde", "En el suelo", "На земле" } },
            { "Carried", new string[] { "Taşınıyor", "La llevan", "Несут" } },
            { "No flag", new string[] { "Bayrak yok", "Sin bandera", "Без флага" } },
            { "No one else in the room has the mod.", new string[] { "Odada başka mod kullanan yok.", "Nadie más en la sala tiene el mod.", "Больше ни у кого в комнате нет мода." } },
            { "Mod users are shown in private rooms.", new string[] { "Mod kullananlar özel odada görünür.", "Los usuarios del mod se ven en salas privadas.", "Игроки с модом видны в приватных комнатах." } },
            { "Off", new string[] { "Kapalı", "Desactivado", "Выкл" } },
            { "Variant is None. Turn it on in Settings.", new string[] { "Değişken Kapalı. Ayarlar'dan aç.", "La variante es Ninguno. Actívala en Ajustes.", "Вариант выключен. Включите в настройках." } },
            { "Only you see it. You don't need to own the flag.", new string[] { "Sadece sen görürsün. Bayrağa sahip olman gerekmez.", "Solo tú la ves. No necesitas tener la bandera.", "Видите только вы. Флаг покупать не нужно." } },
            { "Everyone sees it. You must own and wear this flag in game.", new string[] { "Herkes görür. Bu bayrak oyunda sende olmalı ve takılı olmalı.", "Todos la ven. Debes tener y llevar esta bandera.", "Видят все. Флаг должен быть у вас и надет." } },
            { "Pride Flag not found on your gorilla. Equip it once, then unequip it.", new string[] { "Gorilinde bayrak modeli bulunamadı. Biraz bekle ya da odaya gir.", "No se encontró el modelo de la bandera. Espera un poco.", "Модель флага не найдена. Подождите немного." } },
            { "Mesh dump failed, see log.", new string[] { "Mesh kaydedilemedi, log'a bak.", "Error al guardar la malla, mira el log.", "Ошибка сохранения меша, см. лог." } },
        };

        private static readonly Dictionary<string, string[]> BayrakCeviri = new Dictionary<string, string[]>
        {
            { "algeria", new string[] { "Cezayir", "Argelia", "Алжир" } },
            { "andorra", new string[] { "Andorra", "Andorra", "Андорра" } },
            { "argentina", new string[] { "Arjantin", "Argentina", "Аргентина" } },
            { "armenia", new string[] { "Ermenistan", "Armenia", "Армения" } },
            { "australia", new string[] { "Avustralya", "Australia", "Австралия" } },
            { "austria", new string[] { "Avusturya", "Austria", "Австрия" } },
            { "azerbaijan", new string[] { "Azerbaycan", "Azerbaiyán", "Азербайджан" } },
            { "belarus", new string[] { "Belarus", "Bielorrusia", "Беларусь" } },
            { "belgium", new string[] { "Belçika", "Bélgica", "Бельгия" } },
            { "bosnia_herzegovina", new string[] { "Bosna-Hersek", "Bosnia", "Босния и Герц." } },
            { "brazil", new string[] { "Brezilya", "Brasil", "Бразилия" } },
            { "bulgaria", new string[] { "Bulgaristan", "Bulgaria", "Болгария" } },
            { "canada", new string[] { "Kanada", "Canadá", "Канада" } },
            { "chile", new string[] { "Şili", "Chile", "Чили" } },
            { "china", new string[] { "Çin", "China", "Китай" } },
            { "croatia", new string[] { "Hırvatistan", "Croacia", "Хорватия" } },
            { "czechia", new string[] { "Çekya", "Chequia", "Чехия" } },
            { "denmark", new string[] { "Danimarka", "Dinamarca", "Дания" } },
            { "egypt", new string[] { "Mısır", "Egipto", "Египет" } },
            { "estonia", new string[] { "Estonya", "Estonia", "Эстония" } },
            { "finland", new string[] { "Finlandiya", "Finlandia", "Финляндия" } },
            { "france", new string[] { "Fransa", "Francia", "Франция" } },
            { "georgia", new string[] { "Gürcistan", "Georgia", "Грузия" } },
            { "germany", new string[] { "Almanya", "Alemania", "Германия" } },
            { "greece", new string[] { "Yunanistan", "Grecia", "Греция" } },
            { "hungary", new string[] { "Macaristan", "Hungría", "Венгрия" } },
            { "iceland", new string[] { "İzlanda", "Islandia", "Исландия" } },
            { "india", new string[] { "Hindistan", "India", "Индия" } },
            { "indonesia", new string[] { "Endonezya", "Indonesia", "Индонезия" } },
            { "iran", new string[] { "İran", "Irán", "Иран" } },
            { "ireland", new string[] { "İrlanda", "Irlanda", "Ирландия" } },
            { "israel", new string[] { "İsrail", "Israel", "Израиль" } },
            { "italy", new string[] { "İtalya", "Italia", "Италия" } },
            { "japan", new string[] { "Japonya", "Japón", "Япония" } },
            { "kosovo", new string[] { "Kosova", "Kosovo", "Косово" } },
            { "latvia", new string[] { "Letonya", "Letonia", "Латвия" } },
            { "libya", new string[] { "Libya", "Libia", "Ливия" } },
            { "liechtenstein", new string[] { "Lihtenştayn", "Liechtenstein", "Лихтенштейн" } },
            { "lithuania", new string[] { "Litvanya", "Lituania", "Литва" } },
            { "luxembourg", new string[] { "Lüksemburg", "Luxemburgo", "Люксембург" } },
            { "malaysia", new string[] { "Malezya", "Malasia", "Малайзия" } },
            { "malta", new string[] { "Malta", "Malta", "Мальта" } },
            { "mexico", new string[] { "Meksika", "México", "Мексика" } },
            { "moldova", new string[] { "Moldova", "Moldavia", "Молдова" } },
            { "monaco", new string[] { "Monako", "Mónaco", "Монако" } },
            { "montenegro", new string[] { "Karadağ", "Montenegro", "Черногория" } },
            { "morocco", new string[] { "Fas", "Marruecos", "Марокко" } },
            { "netherlands", new string[] { "Hollanda", "Países Bajos", "Нидерланды" } },
            { "north_macedonia", new string[] { "K. Makedonya", "Macedonia N.", "Сев. Македония" } },
            { "norway", new string[] { "Norveç", "Noruega", "Норвегия" } },
            { "philippines", new string[] { "Filipinler", "Filipinas", "Филиппины" } },
            { "poland", new string[] { "Polonya", "Polonia", "Польша" } },
            { "portugal", new string[] { "Portekiz", "Portugal", "Португалия" } },
            { "romania", new string[] { "Romanya", "Rumania", "Румыния" } },
            { "russia", new string[] { "Rusya", "Rusia", "Россия" } },
            { "san_marino", new string[] { "San Marino", "San Marino", "Сан-Марино" } },
            { "saudi_arabia", new string[] { "S. Arabistan", "Arabia Saudí", "Сауд. Аравия" } },
            { "serbia", new string[] { "Sırbistan", "Serbia", "Сербия" } },
            { "slovakia", new string[] { "Slovakya", "Eslovaquia", "Словакия" } },
            { "slovenia", new string[] { "Slovenya", "Eslovenia", "Словения" } },
            { "south_africa", new string[] { "Güney Afrika", "Sudáfrica", "ЮАР" } },
            { "south_korea", new string[] { "Güney Kore", "Corea del Sur", "Южная Корея" } },
            { "spain", new string[] { "İspanya", "España", "Испания" } },
            { "sweden", new string[] { "İsveç", "Suecia", "Швеция" } },
            { "switzerland", new string[] { "İsviçre", "Suiza", "Швейцария" } },
            { "taiwan", new string[] { "Tayvan", "Taiwán", "Тайвань" } },
            { "thailand", new string[] { "Tayland", "Tailandia", "Таиланд" } },
            { "turkey", new string[] { "Türkiye", "Turquía", "Турция" } },
            { "afghanistan", new string[] { "Afganistan", "Afganistán", "Афганистан" } },
            { "albania", new string[] { "Arnavutluk", "Albania", "Албания" } },
            { "angola", new string[] { "Angola", "Angola", "Ангола" } },
            { "antigua_barbuda", new string[] { "Antigua ve Barbuda", "Antigua y Barbuda", "Антигуа и Барбуда" } },
            { "bahamas", new string[] { "Bahamalar", "Bahamas", "Багамы" } },
            { "bahrain", new string[] { "Bahreyn", "Baréin", "Бахрейн" } },
            { "bangladesh", new string[] { "Bangladeş", "Bangladés", "Бангладеш" } },
            { "barbados", new string[] { "Barbados", "Barbados", "Барбадос" } },
            { "belize", new string[] { "Belize", "Belice", "Белиз" } },
            { "benin", new string[] { "Benin", "Benín", "Бенин" } },
            { "bhutan", new string[] { "Butan", "Bután", "Бутан" } },
            { "bolivia", new string[] { "Bolivya", "Bolivia", "Боливия" } },
            { "botswana", new string[] { "Botsvana", "Botsuana", "Ботсвана" } },
            { "brunei", new string[] { "Brunei", "Brunéi", "Бруней" } },
            { "burkina_faso", new string[] { "Burkina Faso", "Burkina Faso", "Буркина-Фасо" } },
            { "burundi", new string[] { "Burundi", "Burundi", "Бурунди" } },
            { "cambodia", new string[] { "Kamboçya", "Camboya", "Камбоджа" } },
            { "cameroon", new string[] { "Kamerun", "Camerún", "Камерун" } },
            { "cape_verde", new string[] { "Yeşil Burun", "Cabo Verde", "Кабо-Верде" } },
            { "central_african_republic", new string[] { "Orta Afrika Cumhuriyeti", "República Centroafricana", "ЦАР" } },
            { "chad", new string[] { "Çad", "Chad", "Чад" } },
            { "colombia", new string[] { "Kolombiya", "Colombia", "Колумбия" } },
            { "comoros", new string[] { "Komorlar", "Comoras", "Коморы" } },
            { "congo", new string[] { "Kongo", "Congo", "Конго" } },
            { "costa_rica", new string[] { "Kosta Rika", "Costa Rica", "Коста-Рика" } },
            { "cuba", new string[] { "Küba", "Cuba", "Куба" } },
            { "cyprus", new string[] { "Kıbrıs", "Chipre", "Кипр" } },
            { "djibouti", new string[] { "Cibuti", "Yibuti", "Джибути" } },
            { "dominica", new string[] { "Dominika", "Dominica", "Доминика" } },
            { "dominican_republic", new string[] { "Dominik Cumhuriyeti", "República Dominicana", "Доминиканская Республика" } },
            { "dr_congo", new string[] { "Kongo DC", "RD del Congo", "ДР Конго" } },
            { "east_timor", new string[] { "Doğu Timor", "Timor Oriental", "Восточный Тимор" } },
            { "ecuador", new string[] { "Ekvador", "Ecuador", "Эквадор" } },
            { "el_salvador", new string[] { "El Salvador", "El Salvador", "Сальвадор" } },
            { "equatorial_guinea", new string[] { "Ekvator Ginesi", "Guinea Ecuatorial", "Экваториальная Гвинея" } },
            { "eritrea", new string[] { "Eritre", "Eritrea", "Эритрея" } },
            { "eswatini", new string[] { "Esvatini", "Esuatini", "Эсватини" } },
            { "ethiopia", new string[] { "Etiyopya", "Etiopía", "Эфиопия" } },
            { "fiji", new string[] { "Fiji", "Fiyi", "Фиджи" } },
            { "gabon", new string[] { "Gabon", "Gabón", "Габон" } },
            { "gambia", new string[] { "Gambiya", "Gambia", "Гамбия" } },
            { "ghana", new string[] { "Gana", "Ghana", "Гана" } },
            { "grenada", new string[] { "Grenada", "Granada", "Гренада" } },
            { "guatemala", new string[] { "Guatemala", "Guatemala", "Гватемала" } },
            { "guinea", new string[] { "Gine", "Guinea", "Гвинея" } },
            { "guinea_bissau", new string[] { "Gine-Bissau", "Guinea-Bisáu", "Гвинея-Бисау" } },
            { "guyana", new string[] { "Guyana", "Guyana", "Гайана" } },
            { "haiti", new string[] { "Haiti", "Haití", "Гаити" } },
            { "honduras", new string[] { "Honduras", "Honduras", "Гондурас" } },
            { "iraq", new string[] { "Irak", "Irak", "Ирак" } },
            { "ivory_coast", new string[] { "Fildişi Sahili", "Costa de Marfil", "Кот-д'Ивуар" } },
            { "jamaica", new string[] { "Jamaika", "Jamaica", "Ямайка" } },
            { "jordan", new string[] { "Ürdün", "Jordania", "Иордания" } },
            { "kazakhstan", new string[] { "Kazakistan", "Kazajistán", "Казахстан" } },
            { "kenya", new string[] { "Kenya", "Kenia", "Кения" } },
            { "kiribati", new string[] { "Kiribati", "Kiribati", "Кирибати" } },
            { "kuwait", new string[] { "Kuveyt", "Kuwait", "Кувейт" } },
            { "kyrgyzstan", new string[] { "Kırgızistan", "Kirguistán", "Киргизия" } },
            { "laos", new string[] { "Laos", "Laos", "Лаос" } },
            { "lebanon", new string[] { "Lübnan", "Líbano", "Ливан" } },
            { "lesotho", new string[] { "Lesotho", "Lesoto", "Лесото" } },
            { "liberia", new string[] { "Liberya", "Liberia", "Либерия" } },
            { "madagascar", new string[] { "Madagaskar", "Madagascar", "Мадагаскар" } },
            { "malawi", new string[] { "Malavi", "Malaui", "Малави" } },
            { "maldives", new string[] { "Maldivler", "Maldivas", "Мальдивы" } },
            { "mali", new string[] { "Mali", "Malí", "Мали" } },
            { "marshall_islands", new string[] { "Marshall Adaları", "Islas Marshall", "Маршалловы Острова" } },
            { "mauritania", new string[] { "Moritanya", "Mauritania", "Мавритания" } },
            { "mauritius", new string[] { "Mauritius", "Mauricio", "Маврикий" } },
            { "micronesia", new string[] { "Mikronezya", "Micronesia", "Микронезия" } },
            { "mongolia", new string[] { "Moğolistan", "Mongolia", "Монголия" } },
            { "mozambique", new string[] { "Mozambik", "Mozambique", "Мозамбик" } },
            { "myanmar", new string[] { "Myanmar", "Myanmar", "Мьянма" } },
            { "namibia", new string[] { "Namibya", "Namibia", "Намибия" } },
            { "nauru", new string[] { "Nauru", "Nauru", "Науру" } },
            { "nepal", new string[] { "Nepal", "Nepal", "Непал" } },
            { "new_zealand", new string[] { "Yeni Zelanda", "Nueva Zelanda", "Новая Зеландия" } },
            { "nicaragua", new string[] { "Nikaragua", "Nicaragua", "Никарагуа" } },
            { "niger", new string[] { "Nijer", "Níger", "Нигер" } },
            { "nigeria", new string[] { "Nijerya", "Nigeria", "Нигерия" } },
            { "north_korea", new string[] { "Kuzey Kore", "Corea del Norte", "КНДР" } },
            { "oman", new string[] { "Umman", "Omán", "Оман" } },
            { "pakistan", new string[] { "Pakistan", "Pakistán", "Пакистан" } },
            { "palau", new string[] { "Palau", "Palaos", "Палау" } },
            { "palestine", new string[] { "Filistin", "Palestina", "Палестина" } },
            { "panama", new string[] { "Panama", "Panamá", "Панама" } },
            { "papua_new_guinea", new string[] { "Papua Yeni Gine", "Papúa Nueva Guinea", "Папуа — Новая Гвинея" } },
            { "paraguay", new string[] { "Paraguay", "Paraguay", "Парагвай" } },
            { "peru", new string[] { "Peru", "Perú", "Перу" } },
            { "qatar", new string[] { "Katar", "Catar", "Катар" } },
            { "rwanda", new string[] { "Ruanda", "Ruanda", "Руанда" } },
            { "saint_kitts_nevis", new string[] { "Saint Kitts ve Nevis", "San Cristóbal y Nieves", "Сент-Китс и Невис" } },
            { "saint_lucia", new string[] { "Saint Lucia", "Santa Lucía", "Сент-Люсия" } },
            { "saint_vincent", new string[] { "Saint Vincent", "San Vicente", "Сент-Винсент" } },
            { "samoa", new string[] { "Samoa", "Samoa", "Самоа" } },
            { "sao_tome_principe", new string[] { "São Tomé ve Príncipe", "Santo Tomé y Príncipe", "Сан-Томе и Принсипи" } },
            { "senegal", new string[] { "Senegal", "Senegal", "Сенегал" } },
            { "seychelles", new string[] { "Seyşeller", "Seychelles", "Сейшелы" } },
            { "sierra_leone", new string[] { "Sierra Leone", "Sierra Leona", "Сьерра-Леоне" } },
            { "singapore", new string[] { "Singapur", "Singapur", "Сингапур" } },
            { "solomon_islands", new string[] { "Solomon Adaları", "Islas Salomón", "Соломоновы Острова" } },
            { "somalia", new string[] { "Somali", "Somalia", "Сомали" } },
            { "south_sudan", new string[] { "Güney Sudan", "Sudán del Sur", "Южный Судан" } },
            { "sri_lanka", new string[] { "Sri Lanka", "Sri Lanka", "Шри-Ланка" } },
            { "sudan", new string[] { "Sudan", "Sudán", "Судан" } },
            { "suriname", new string[] { "Surinam", "Surinam", "Суринам" } },
            { "syria", new string[] { "Suriye", "Siria", "Сирия" } },
            { "tajikistan", new string[] { "Tacikistan", "Tayikistán", "Таджикистан" } },
            { "tanzania", new string[] { "Tanzanya", "Tanzania", "Танзания" } },
            { "togo", new string[] { "Togo", "Togo", "Того" } },
            { "tonga", new string[] { "Tonga", "Tonga", "Тонга" } },
            { "trinidad_tobago", new string[] { "Trinidad ve Tobago", "Trinidad y Tobago", "Тринидад и Тобаго" } },
            { "tunisia", new string[] { "Tunus", "Túnez", "Тунис" } },
            { "turkmenistan", new string[] { "Türkmenistan", "Turkmenistán", "Туркменистан" } },
            { "tuvalu", new string[] { "Tuvalu", "Tuvalu", "Тувалу" } },
            { "uganda", new string[] { "Uganda", "Uganda", "Уганда" } },
            { "uruguay", new string[] { "Uruguay", "Uruguay", "Уругвай" } },
            { "uzbekistan", new string[] { "Özbekistan", "Uzbekistán", "Узбекистан" } },
            { "vanuatu", new string[] { "Vanuatu", "Vanuatu", "Вануату" } },
            { "venezuela", new string[] { "Venezuela", "Venezuela", "Венесуэла" } },
            { "yemen", new string[] { "Yemen", "Yemen", "Йемен" } },
            { "zambia", new string[] { "Zambiya", "Zambia", "Замбия" } },
            { "zimbabwe", new string[] { "Zimbabve", "Zimbabue", "Зимбабве" } },
            { "northern_cyprus", new string[] { "KKTC", "RTCN", "ТРСК" } },
            { "east_turkestan", new string[] { "Doğu Türkistan", "Turquestán Oriental", "Восточный Туркестан" } },
            { "tibet", new string[] { "Tibet", "Tíbet", "Тибет" } },
            { "aceh", new string[] { "Aceh", "Aceh", "Ачех" } },
            { "yogyakarta", new string[] { "Yogyakarta", "Yogyakarta", "Джокьякарта" } },
            { "papua", new string[] { "Papua", "Papúa", "Папуа" } },
            { "west_papua", new string[] { "Batı Papua", "Papúa Occidental", "Западное Папуа" } },
            { "bangsamoro", new string[] { "Bangsamoro", "Bangsamoro", "Бангсаморо" } },
            { "gorno_badakhshan", new string[] { "Dağlık Badahşan", "Alto Badajshán", "Горно-Бадахшан" } },
            { "karakalpakstan", new string[] { "Karakalpakistan", "Karakalpakia", "Каракалпакстан" } },
            { "bashkortostan", new string[] { "Başkurdistan", "Baskortostán", "Башкортостан" } },
            { "chuvashia", new string[] { "Çuvaşistan", "Chuvasia", "Чувашия" } },
            { "tuva", new string[] { "Tuva", "Tuvá", "Тува" } },
            { "sakha", new string[] { "Yakutistan", "Sajá (Yakutia)", "Якутия" } },
            { "altai_republic", new string[] { "Altay", "República de Altái", "Республика Алтай" } },
            { "khakassia", new string[] { "Hakasya", "Jakasia", "Хакасия" } },
            { "chechnya", new string[] { "Çeçenistan", "Chechenia", "Чечня" } },
            { "dagestan", new string[] { "Dağıstan", "Daguestán", "Дагестан" } },
            { "ingushetia", new string[] { "İnguşetya", "Ingusetia", "Ингушетия" } },
            { "komi", new string[] { "Komi", "Komi", "Коми" } },
            { "karelia", new string[] { "Karelya", "Carelia", "Карелия" } },
            { "jewish_ao", new string[] { "Yahudi Özerk Oblastı", "Óblast Autónomo Hebreo", "Еврейская АО" } },
            { "chukotka", new string[] { "Çukotka", "Chukotka", "Чукотка" } },
            { "transnistria", new string[] { "Transdinyester", "Transnistria", "Приднестровье" } },
            { "crimea", new string[] { "Kırım", "Crimea", "Крым" } },
            { "adjara", new string[] { "Acaristan", "Ayaria", "Аджария" } },
            { "abkhazia", new string[] { "Abhazya", "Abjasia", "Абхазия" } },
            { "south_ossetia", new string[] { "Güney Osetya", "Osetia del Sur", "Южная Осетия" } },
            { "mount_athos", new string[] { "Aynoroz", "Monte Athos", "Афон" } },
            { "vojvodina", new string[] { "Voyvodina", "Voivodina", "Воеводина" } },
            { "sicily", new string[] { "Sicilya", "Sicilia", "Сицилия" } },
            { "sardinia", new string[] { "Sardinya", "Cerdeña", "Сардиния" } },
            { "aosta_valley", new string[] { "Aosta Vadisi", "Valle de Aosta", "Валле-д'Аоста" } },
            { "friuli", new string[] { "Friuli-Venezia Giulia", "Friul-Venecia Julia", "Фриули-Венеция-Джулия" } },
            { "trentino", new string[] { "Trentino-Alto Adige", "Trentino-Alto Adigio", "Трентино-Альто-Адидже" } },
            { "andalusia", new string[] { "Endülüs", "Andalucía", "Андалусия" } },
            { "azores", new string[] { "Azor Adaları", "Azores", "Азорские острова" } },
            { "madeira", new string[] { "Madeira", "Madeira", "Мадейра" } },
            { "northern_mariana", new string[] { "Kuzey Mariana Adaları", "Islas Marianas del Norte", "Северные Марианские острова" } },
            { "guam", new string[] { "Guam", "Guam", "Гуам" } },
            { "american_samoa", new string[] { "Amerikan Samoası", "Samoa Americana", "Американское Самоа" } },
            { "us_virgin_islands", new string[] { "ABD Virjin Adaları", "Islas Vírgenes de EE. UU.", "Виргинские острова (США)" } },
            { "barbuda", new string[] { "Barbuda", "Barbuda", "Барбуда" } },
            { "nevis", new string[] { "Nevis", "Nieves", "Невис" } },
            { "zanzibar", new string[] { "Zanzibar", "Zanzíbar", "Занзибар" } },
            { "bougainville", new string[] { "Bougainville", "Bougainville", "Бугенвиль" } },
            { "niue", new string[] { "Niue", "Niue", "Ниуэ" } },
            { "rodrigues", new string[] { "Rodrigues", "Rodrigues", "Родригес" } },
            { "principe", new string[] { "Príncipe", "Príncipe", "Принсипи" } },
            { "north_caribbean_coast", new string[] { "Kuzey Karayip Kıyısı", "Costa Caribe Norte", "Северное Карибское побережье" } },
            { "south_caribbean_coast", new string[] { "Güney Karayip Kıyısı", "Costa Caribe Sur", "Южное Карибское побережье" } },
            { "un", new string[] { "BM", "ONU", "ООН" } },
            { "nato", new string[] { "NATO", "OTAN", "НАТО" } },
            { "sco", new string[] { "ŞİÖ", "OCS", "ШОС" } },
            { "csto", new string[] { "KGAÖ", "OTSC", "ОДКБ" } },
            { "eu", new string[] { "AB", "UE", "ЕС" } },
            { "council_of_europe", new string[] { "Avrupa Konseyi", "Consejo de Europa", "Совет Европы" } },
            { "turkic_states", new string[] { "Türk Devletleri Teşkilatı", "Org. de Estados Túrquicos", "Орг. тюркских государств" } },
            { "oic", new string[] { "İİT", "OCI", "ОИС" } },
            { "arab_league", new string[] { "Arap Birliği", "Liga Árabe", "Лига арабских государств" } },
            { "african_union", new string[] { "Afrika Birliği", "Unión Africana", "Африканский союз" } },
            { "asean", new string[] { "ASEAN", "ASEAN", "АСЕАН" } },
            { "cis", new string[] { "BDT", "CEI", "СНГ" } },
            { "scotland", new string[] { "İskoçya", "Escocia", "Шотландия" } },
            { "wales", new string[] { "Galler", "Gales", "Уэльс" } },
            { "northern_ireland", new string[] { "Kuzey İrlanda", "Irlanda del Norte", "Северная Ирландия" } },
            { "aland", new string[] { "Åland", "Åland", "Аланды" } },
            { "aruba", new string[] { "Aruba", "Aruba", "Аруба" } },
            { "basque_country", new string[] { "Bask Bölgesi", "País Vasco", "Страна Басков" } },
            { "bermuda", new string[] { "Bermuda", "Bermudas", "Бермуды" } },
            { "catalonia", new string[] { "Katalonya", "Cataluña", "Каталония" } },
            { "cayman_islands", new string[] { "Cayman Adaları", "Islas Caimán", "Каймановы острова" } },
            { "cook_islands", new string[] { "Cook Adaları", "Islas Cook", "Острова Кука" } },
            { "curacao", new string[] { "Curaçao", "Curazao", "Кюрасао" } },
            { "faroe_islands", new string[] { "Faroe Adaları", "Islas Feroe", "Фарерские острова" } },
            { "french_polynesia", new string[] { "Fransız Polinezyası", "Polinesia Francesa", "Французская Полинезия" } },
            { "gagauzia", new string[] { "Gagavuzya", "Gagauzia", "Гагаузия" } },
            { "galicia", new string[] { "Galiçya", "Galicia", "Галисия" } },
            { "gibraltar", new string[] { "Cebelitarık", "Gibraltar", "Гибралтар" } },
            { "greenland", new string[] { "Grönland", "Groenlandia", "Гренландия" } },
            { "guernsey", new string[] { "Guernsey", "Guernsey", "Гернси" } },
            { "hong_kong", new string[] { "Hong Kong", "Hong Kong", "Гонконг" } },
            { "isle_of_man", new string[] { "Man Adası", "Isla de Man", "Остров Мэн" } },
            { "jersey", new string[] { "Jersey", "Jersey", "Джерси" } },
            { "kurdistan_region", new string[] { "Kürdistan Bölgesi", "Región del Kurdistán", "Иракский Курдистан" } },
            { "macau", new string[] { "Makao", "Macao", "Макао" } },
            { "new_caledonia", new string[] { "Yeni Kaledonya", "Nueva Caledonia", "Новая Каледония" } },
            { "puerto_rico", new string[] { "Porto Riko", "Puerto Rico", "Пуэрто-Рико" } },
            { "sint_maarten", new string[] { "Sint Maarten", "San Martín", "Синт-Мартен" } },
            { "tatarstan", new string[] { "Tataristan", "Tatarstán", "Татарстан" } },
            { "uae", new string[] { "BAE", "EAU", "ОАЭ" } },
            { "ukraine", new string[] { "Ukrayna", "Ucrania", "Украина" } },
            { "united_kingdom", new string[] { "Birleşik Krallık", "Reino Unido", "Великобритания" } },
            { "usa", new string[] { "ABD", "EE. UU.", "США" } },
            { "vatican", new string[] { "Vatikan", "Vaticano", "Ватикан" } },
            { "vietnam", new string[] { "Vietnam", "Vietnam", "Вьетнам" } },
            { "rainbow", new string[] { "Gökkuşağı", "Arcoíris", "Радуга" } },
            { "progress", new string[] { "Progress", "Progreso", "Прогресс" } },
            { "progress_intersex", new string[] { "İnterseks Prog.", "Progreso intersex", "Прогресс интер." } },
            { "philadelphia", new string[] { "Philadelphia", "Filadelfia", "Филадельфия" } },
            { "original_8", new string[] { "Orijinal 8", "Original 8", "Оригинал 8" } },
            { "trans", new string[] { "Trans", "Trans", "Транс" } },
            { "bisexual", new string[] { "Biseksüel", "Bisexual", "Бисексуал" } },
            { "lesbian", new string[] { "Lezbiyen", "Lésbica", "Лесбиянки" } },
            { "pansexual", new string[] { "Panseksüel", "Pansexual", "Пансексуал" } },
            { "nonbinary", new string[] { "Non-binary", "No binario", "Небинарный" } },
            { "asexual", new string[] { "Aseksüel", "Asexual", "Асексуал" } },
            { "gay_men", new string[] { "Gey erkek", "Gay", "Геи" } },
            { "intersex", new string[] { "İnterseks", "Intersexual", "Интерсекс" } },
            { "genderfluid", new string[] { "Genderfluid", "Género fluido", "Гендерфлюид" } },
            { "genderqueer", new string[] { "Genderqueer", "Genderqueer", "Гендерквир" } },
            { "agender", new string[] { "Agender", "Agénero", "Агендер" } },
            { "aromantic", new string[] { "Aromantik", "Arromántico", "Аромантик" } },
            { "demisexual", new string[] { "Demiseksüel", "Demisexual", "Демисексуал" } },
            { "polysexual", new string[] { "Poliseksüel", "Polisexual", "Полисексуал" } },
            { "omnisexual", new string[] { "Omniseksüel", "Omnisexual", "Омнисексуал" } },
            { "bigender", new string[] { "Bigender", "Bigénero", "Бигендер" } },
            { "two_spirit", new string[] { "İki Ruhlu", "Dos Espíritus", "Две души" } },
            { "straight_ally", new string[] { "Destekçi", "Aliado", "Союзник" } },
        };

        private string Dil(string en)
        {
            if (dil <= 0) return en;
            string[] a;
            if (Ceviri.TryGetValue(en, out a) && dil - 1 < a.Length) return a[dil - 1];
            return en;
        }

        private static readonly Dictionary<string, string> IngAd = new Dictionary<string, string>
        {
            { "dr_congo", "DR Congo" },
            { "antigua_barbuda", "Antigua & Barbuda" },
            { "saint_kitts_nevis", "St Kitts & Nevis" },
            { "saint_vincent", "St Vincent" },
            { "sao_tome_principe", "São Tomé" },
            { "trinidad_tobago", "Trinidad & Tobago" },
            { "guinea_bissau", "Guinea-Bissau" },
            { "aland", "Åland" },
            { "curacao", "Curaçao" },
            { "central_african_republic", "Central African Rep." },
            { "papua_new_guinea", "Papua New Guinea" },
            { "north_korea", "North Korea" },
            { "northern_cyprus", "TRNC" },
            { "east_turkestan", "East Turkestan" },
            { "tibet", "Tibet" },
            { "aceh", "Aceh" },
            { "yogyakarta", "Yogyakarta" },
            { "papua", "Papua" },
            { "west_papua", "West Papua" },
            { "bangsamoro", "Bangsamoro" },
            { "gorno_badakhshan", "Gorno-Badakhshan" },
            { "karakalpakstan", "Karakalpakstan" },
            { "bashkortostan", "Bashkortostan" },
            { "chuvashia", "Chuvashia" },
            { "tuva", "Tuva" },
            { "sakha", "Sakha (Yakutia)" },
            { "altai_republic", "Altai Republic" },
            { "khakassia", "Khakassia" },
            { "chechnya", "Chechnya" },
            { "dagestan", "Dagestan" },
            { "ingushetia", "Ingushetia" },
            { "komi", "Komi" },
            { "karelia", "Karelia" },
            { "jewish_ao", "Jewish Autonomous Oblast" },
            { "chukotka", "Chukotka" },
            { "transnistria", "Transnistria" },
            { "crimea", "Crimea" },
            { "adjara", "Adjara" },
            { "abkhazia", "Abkhazia" },
            { "south_ossetia", "South Ossetia" },
            { "mount_athos", "Mount Athos" },
            { "vojvodina", "Vojvodina" },
            { "sicily", "Sicily" },
            { "sardinia", "Sardinia" },
            { "aosta_valley", "Aosta Valley" },
            { "friuli", "Friuli-Venezia Giulia" },
            { "trentino", "Trentino-South Tyrol" },
            { "andalusia", "Andalusia" },
            { "azores", "Azores" },
            { "madeira", "Madeira" },
            { "northern_mariana", "Northern Mariana Islands" },
            { "guam", "Guam" },
            { "american_samoa", "American Samoa" },
            { "us_virgin_islands", "US Virgin Islands" },
            { "barbuda", "Barbuda" },
            { "nevis", "Nevis" },
            { "zanzibar", "Zanzibar" },
            { "bougainville", "Bougainville" },
            { "niue", "Niue" },
            { "rodrigues", "Rodrigues" },
            { "principe", "Príncipe" },
            { "north_caribbean_coast", "North Caribbean Coast" },
            { "south_caribbean_coast", "South Caribbean Coast" },
            { "un", "UN" },
            { "nato", "NATO" },
            { "sco", "SCO" },
            { "csto", "CSTO" },
            { "eu", "EU" },
            { "council_of_europe", "Council of Europe" },
            { "turkic_states", "Organization of Turkic States" },
            { "oic", "OIC" },
            { "arab_league", "Arab League" },
            { "african_union", "African Union" },
            { "asean", "ASEAN" },
            { "cis", "CIS" },
        };

        private string AdTam(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string[] a;
            if (dil > 0 && BayrakCeviri.TryGetValue(s.ToLowerInvariant(), out a)) return a[dil - 1];
            string l = s.ToLowerInvariant();
            if (l == "usa" || l == "uae" || l == "uk") return l.ToUpperInvariant();
            string ing;
            if (dil == 0 && IngAd.TryGetValue(l, out ing)) return ing;
            string[] parca = s.Replace('_', ' ').Split(' ');
            for (int i = 0; i < parca.Length; i++)
                if (parca[i].Length > 0) parca[i] = char.ToUpper(parca[i][0]) + parca[i].Substring(1);
            return string.Join(" ", parca);
        }

        private string Baslikla(string s)
        {
            string r = AdTam(s);
            if (r != null && r.Length > 14) r = r.Substring(0, 13) + "..";
            return r;
        }

        // ---------- sekmeler ----------

        private static bool Eslesir(string ad, string q)
        {
            if (string.IsNullOrEmpty(q)) return true;
            return Temizle(ad).Contains(Temizle(q));
        }

        private int KatSayisi(int k)
        {
            int n = 0;
            for (int i = 0; i < kategoriler.Count; i++) if (kategoriler[i] == k) n++;
            return n;
        }

        private string FlagsKlasor()
        {
            return Path.Combine(Path.GetDirectoryName(Info.Location), "Flags");
        }

        private string KatKlasor(int k)
        {
            if (k == KatOrg) return Path.Combine(FlagsKlasor(), "Organizations");
            if (k < SabitKat) return FlagsKlasor();
            return Path.Combine(FlagsKlasor(), katListesi[k]);
        }

        // hazir bayragin kalici anahtari: "countries/turkey", "pride/rainbow"
        private string Anahtar(int i)
        {
            if (refler.Count > i && refler[i] != null) return refler[i];
            return KatOnek[Mathf.Clamp(kategoriler[i], 0, SabitKat - 1)] + isimler[i];
        }

        private void ListeKaydet(int k, IEnumerable<string> keys)
        {
            string klasor = KatKlasor(k);
            Directory.CreateDirectory(klasor);
            File.WriteAllLines(Path.Combine(klasor, "flags.txt"), new List<string>(keys).ToArray());
        }

        private List<string> KatRefleri(int k)
        {
            List<string> l = new List<string>();
            for (int i = 0; i < isimler.Count; i++)
                if (kategoriler[i] == k && refler[i] != null) l.Add(refler[i]);
            return l;
        }

        private void SecimAc(int k)
        {
            secimModu = true;
            secimKat = k;
            secilenler.Clear();
            if (k >= SabitKat) foreach (string r in KatRefleri(k)) secilenler.Add(r);
            arama = "";
            kaydirma = Vector2.zero;
            yeniKatAcik = k < 0;
            yeniKatAd = "";
        }

        private void SecimKapat()
        {
            secimModu = false;
            yeniKatAcik = false;
            secilenler.Clear();
            arama = "";
            kaydirma = Vector2.zero;
        }

        private void SecimKaydet()
        {
            try
            {
                if (secimKat < 0)
                {
                    string ad = (yeniKatAd ?? "").Trim();
                    foreach (char c in Path.GetInvalidFileNameChars()) ad = ad.Replace(c.ToString(), "");
                    if (ad == "") { durum = Dil("Category name..."); return; }
                    Directory.CreateDirectory(Path.Combine(FlagsKlasor(), ad));
                    File.WriteAllLines(Path.Combine(Path.Combine(FlagsKlasor(), ad), "flags.txt"), new List<string>(secilenler).ToArray());
                    SecimKapat();
                    PngleriYukle();
                    for (int k = 0; k < katListesi.Count; k++) if (katListesi[k] == ad) aktifKat = k;
                }
                else
                {
                    string katAdi = katListesi[secimKat];
                    ListeKaydet(secimKat, secilenler);
                    SecimKapat();
                    PngleriYukle();
                    for (int k = 0; k < katListesi.Count; k++) if (katListesi[k] == katAdi) aktifKat = k;
                }
                durum = "";
            }
            catch (Exception ex) { durum = ex.Message; }
        }

        private string KatGorunen(int k)
        {
            return k < KatGorunAd.Length ? Dil(KatGorunAd[k]) : katListesi[k];
        }

        private void KategoriOlustur()
        {
            SecimKaydet();
            return;
#pragma warning disable CS0162
        }

        private void KategoriOlusturEski()
        {
            string ad = (yeniKatAd ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) ad = ad.Replace(c.ToString(), "");
            if (ad == "") return;
            try
            {
                Directory.CreateDirectory(Path.Combine(FlagsKlasor(), ad));
                yeniKatAcik = false;
                yeniKatAd = "";
                PngleriYukle();
                for (int k = 0; k < katListesi.Count; k++) if (katListesi[k] == ad) aktifKat = k;
                kaydirma = Vector2.zero;
            }
            catch (Exception ex) { durum = ex.Message; }
        }

        private void KategoriSil(int k)
        {
            if (k < SabitKat || k >= katListesi.Count) return;
            try
            {
                if (secili >= 0 && kategoriler[secili] == k) Orijinale();
                Directory.Delete(KatKlasor(k), true);
                aktifKat = 0;
                PngleriYukle();
                aktifKat = 0;
                kaydirma = Vector2.zero;
                durum = "";
            }
            catch (Exception ex) { durum = ex.Message; }
        }

        private void ResimSil(int i)
        {
            if (i < 0 || i >= isimler.Count || HazirKat(kategoriler[i])) return;
            try
            {
                if (secili == i) Orijinale();
                int k = kategoriler[i];
                if (refler[i] != null)
                {
                    List<string> l = KatRefleri(k);
                    l.Remove(refler[i]);
                    ListeKaydet(k, l);
                }
                else
                {
                    string yol = Path.Combine(KatKlasor(k), isimler[i] + ".png");
                    if (File.Exists(yol)) File.Delete(yol);
                }
                PngleriYukle();
            }
            catch (Exception ex) { durum = ex.Message; }
        }

        // Flags sekmesinin sabit ust kismi: kaydirilabilir kategori cipleri + arama kutusu
        private float BayrakUstu(float ix, float y, float iw, Event e)
        {
            Rect satir = new Rect(ix, y, iw, 30f);

            // genislikleri hesapla
            int n = katListesi.Count;
            float[] gen = new float[n + 1];
            string[] yazi = new string[n + 1];
            float toplam = 0f;
            for (int k = 0; k < n; k++)
            {
                yazi[k] = KatGorunen(KatSira(k)) + "  " + KatSayisi(KatSira(k));
                gen[k] = k < n ? sOrta.CalcSize(new GUIContent(yazi[k])).x + 22f : 34f;
                toplam += gen[k] + 6f;
            }
            float maxK = Mathf.Max(0f, toplam - 6f - iw);

            // tekerlek / surukleme ile yatay kaydirma
            if (e.type == EventType.ScrollWheel && satir.Contains(e.mousePosition) && maxK > 0f)
            {
                chipKaydir += (Mathf.Abs(e.delta.x) > Mathf.Abs(e.delta.y) ? e.delta.x : e.delta.y) * 24f;
                e.Use();
            }
            if (e.type == EventType.MouseDown && e.button == 0 && satir.Contains(e.mousePosition))
            {
                chipSurukle = true;
                chipSurukleX = e.mousePosition.x;
            }
            else if (e.type == EventType.MouseDrag && chipSurukle && maxK > 0f)
            {
                chipKaydir -= e.mousePosition.x - chipSurukleX;
                chipSurukleX = e.mousePosition.x;
                e.Use();
            }
            else if (e.type == EventType.MouseUp) chipSurukle = false;
            chipKaydir = Mathf.Clamp(chipKaydir, 0f, maxK);

            GUI.BeginGroup(satir);
            float x = -chipKaydir;
            for (int k = 0; k < n; k++)
            {
                Rect r = new Rect(x, 0f, gen[k], 30f);
                if (k < n)
                {
                    int kk = KatSira(k);
                    bool on = kk == aktifKat && arama == "";
                    GUI.Box(r, GUIContent.none, on ? sChipOn : sKutu);
                    GUI.Label(r, yazi[k], on ? sOrtaBeyaz : sOrtaSoluk);
                    if (GUI.Button(r, GUIContent.none, sBos))
                    {
                        aktifKat = kk; arama = ""; kaydirma = Vector2.zero; GUI.FocusControl(null);
                        katSilBekleyen = -1; silBekleyen = -1;
                        if (secimModu) SecimKapat();
                    }
                }
                else
                {
                    GUI.Box(r, GUIContent.none, (secimModu && secimKat < 0) ? sChipOn : sKutu);
                    GUI.Label(r, "+", sOrtaBeyaz);
                    if (GUI.Button(r, GUIContent.none, sBos)) { if (secimModu && secimKat < 0) SecimKapat(); else SecimAc(-1); }
                }
                x += gen[k] + 6f;
            }
            GUI.EndGroup();

            // kenarda daha fazlasi oldugunu gosteren ince cubuk
            if (maxK > 0f)
            {
                float bw = iw * iw / (toplam - 6f);
                float bx = ix + (iw - bw) * (chipKaydir / maxK);
                GUI.Box(new Rect(bx, y + 33f, bw, 3f), GUIContent.none, sIncePolu);
                y += 6f;
            }
            y += 40f;

            bool odak1 = false, odak2 = false;
            string odakAdi = GUI.GetNameOfFocusedControl();

            if (yeniKatAcik)
            {
                float bw = 74f;
                Rect kr = new Rect(ix, y, iw - bw - 8f, 38f);
                if (e.type == EventType.MouseDown && !kr.Contains(e.mousePosition) && odakAdi == "gfKat") GUI.FocusControl(null);
                GUI.SetNextControlName("gfKat");
                yeniKatAd = GUI.TextField(kr, yeniKatAd, 24, sArama);
                odak2 = GUI.GetNameOfFocusedControl() == "gfKat";
                if (yeniKatAd == "" && !odak2)
                    GUI.Label(new Rect(kr.x + 14f, kr.y, kr.width - 20f, kr.height), Dil("Category name..."), sAramaYer);
                if (odak2 && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) { SecimKaydet(); e.Use(); }

                Rect ob = new Rect(ix + iw - bw, y, bw, 38f);
                GUI.Box(ob, GUIContent.none, sTabOn);
                GUI.Label(ob, Dil("Create"), sOrtaBeyaz);
                if (Tikla(ob)) SecimKaydet();
                y += 46f;
            }

            Rect sr = new Rect(ix, y, iw, 38f);
            if (e.type == EventType.MouseDown && !sr.Contains(e.mousePosition) && odakAdi == "gfArama") GUI.FocusControl(null);

            GUI.SetNextControlName("gfArama");
            string yeni = GUI.TextField(sr, arama, 40, sArama);
            if (yeni != arama) { arama = yeni; kaydirma = Vector2.zero; }
            odak1 = GUI.GetNameOfFocusedControl() == "gfArama";
            aramaOdak = odak1 || GUI.GetNameOfFocusedControl() == "gfKat";

            if (arama == "" && !odak1)
                GUI.Label(new Rect(sr.x + 14f, sr.y, sr.width - 20f, sr.height), Dil("Search flags..."), sAramaYer);

            return y + 48f;
        }

        private float SecimEkrani(float iw)
        {
            float cy = 0f;
            string baslik = secimKat < 0 ? Dil("Select flags") : Dil("Select flags") + " - " + katListesi[secimKat];
            EtiketYaz(baslik.ToUpperInvariant() + "  (" + secilenler.Count + ")", cy, iw);
            cy += 26f;

            float bosluk = 10f;
            float kw = (iw - bosluk * 2f) / 3f;
            int n = 0;
            for (int i = 0; i < bayraklar.Count; i++)
            {
                if (!HazirKat(kategoriler[i])) continue;
                if (arama != "" && !(Eslesir(isimler[i], arama) || Eslesir(AdTam(isimler[i]), arama))) continue;

                float x = (n % 3) * (kw + bosluk);
                float ty = cy + (n / 3) * 100f;
                Rect fr = new Rect(x, ty, kw, kw * 0.75f);
                string key = Anahtar(i);
                bool on = secilenler.Contains(key);

                if (on) GUI.Box(new Rect(fr.x - 3, fr.y - 3, fr.width + 6, fr.height + 6), GUIContent.none, sKartSecili);
                if (i < thumbs.Count && thumbs[i] != null) GUI.DrawTexture(fr, thumbs[i], ScaleMode.StretchToFill, true);
                if (on)
                {
                    Rect cr = new Rect(fr.xMax - 20f, fr.y + 2f, 18f, 18f);
                    GUI.Box(cr, GUIContent.none, sMiniKirmizi);
                    Color eskiR = GUI.color; GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(cr.x + 5f, cr.y + 5f, 8f, 8f), tDaire);
                    GUI.color = eskiR;
                }
                GUI.Label(new Rect(x - 4, fr.yMax + 4f, kw + 8, 18), Baslikla(isimler[i]), on ? sOrtaSecili : sOrtaKucuk);
                if (GUI.Button(new Rect(x, ty, kw, fr.height + 22f), GUIContent.none, sBos))
                {
                    if (on) secilenler.Remove(key); else secilenler.Add(key);
                }
                n++;
            }
            cy += ((n + 2) / 3) * 100f + 2f;

            float bw = (iw - 8f) / 2f;
            Rect ok = new Rect(0, cy, bw, 42f);
            GUI.Box(ok, GUIContent.none, sTabOn);
            GUI.Label(ok, secimKat < 0 ? Dil("Create") : Dil("Save"), sOrtaBeyaz);
            if (Tikla(ok)) { SecimKaydet(); return cy + 50f; }

            Rect ip = new Rect(bw + 8f, cy, bw, 42f);
            GUI.Box(ip, GUIContent.none, sKutu);
            GUI.Label(ip, Dil("Cancel"), sOrta);
            if (Tikla(ip)) SecimKapat();
            cy += 50f;

            if (durum != "")
            {
                GUI.Label(new Rect(4, cy, iw - 8, 36), durum, sAltSatir);
                cy += 40f;
            }
            return cy;
        }

        private float SekmeBayraklar(float iw)
        {
            if (secimModu) return SecimEkrani(iw);

            float cy = 0f;
            bool araniyor = arama != "";

            List<int> goster = new List<int>();
            for (int i = 0; i < bayraklar.Count; i++)
            {
                if (araniyor) { if (refler[i] == null && (Eslesir(isimler[i], arama) || Eslesir(AdTam(isimler[i]), arama))) goster.Add(i); }
                else if (kategoriler[i] == aktifKat) goster.Add(i);
            }

            if (aktifKat >= katListesi.Count) aktifKat = 0;
            EtiketYaz(araniyor ? Dil("RESULTS") : KatGorunen(aktifKat).ToUpperInvariant(), cy, iw);

            cy += 26f;

            if (!araniyor && aktifKat >= SabitKat)
            {
                bool bekliyor = katSilBekleyen == aktifKat && Time.time - katSilZaman < 4f;
                float hb = (iw - 8f) / 2f;
                Rect eb = new Rect(0f, cy, hb, 34f);
                Rect kb = new Rect(hb + 8f, cy, hb, 34f);

                GUI.Box(eb, GUIContent.none, sKutu);
                GUI.Label(eb, Dil("Edit"), sOrta);
                if (Tikla(eb)) { SecimAc(aktifKat); return cy + 40f; }

                GUI.Box(kb, GUIContent.none, bekliyor ? sTabOn : sKutu);
                GUI.Label(kb, bekliyor ? Dil("Click again to delete") : Dil("Delete category"), bekliyor ? sOrtaBeyaz : sSilYazi13);
                if (Tikla(kb))
                {
                    if (bekliyor) { katSilBekleyen = -1; KategoriSil(aktifKat); return cy + 40f; }
                    katSilBekleyen = aktifKat; katSilZaman = Time.time;
                }
                cy += 44f;
            }


            if (!araniyor && !HazirKat(aktifKat))
            {
                GUI.Label(new Rect(4, cy - 4, iw - 8, 20), aktifKat == KatOzel ? Dil("Images you add with \"Add image\" show up here.") : Dil("Add flags with Edit, or your own images with \"Add image\"."), sAltSatir);
                cy += 24f;
            }

            if (araniyor && goster.Count == 0)
            {
                GUI.Label(new Rect(4, cy, iw, 20), Dil("No flags found."), sAltSatir);
                cy += 28f;
            }

            float bosluk = 10f;
            float kw = (iw - bosluk * 2f) / 3f;
            bool arti = !araniyor && !HazirKat(aktifKat);
            int adet = goster.Count + (arti ? 1 : 0);

            for (int n = 0; n < adet; n++)
            {
                float x = (n % 3) * (kw + bosluk);
                float ty = cy + (n / 3) * 100f;
                Rect fr = new Rect(x, ty, kw, kw * 0.75f);

                if (n == goster.Count)
                {
                    // Custom: "+" karti
                    GUI.Box(fr, GUIContent.none, sKutu);
                    GUI.Label(fr, "+", sOrtaBuyuk);
                    GUI.Label(new Rect(x - 4, fr.yMax + 4f, kw + 8, 18), Dil("Add image"), sOrtaKucuk);
                    if (GUI.Button(new Rect(x, ty, kw, fr.height + 22f), GUIContent.none, sBos)) resimEkle = true;
                    continue;
                }

                int i = goster[n];
                bool sec = i == secili;

                if (sec) GUI.Box(new Rect(fr.x - 3, fr.y - 3, fr.width + 6, fr.height + 6), GUIContent.none, sKartSecili);

                if (i < thumbs.Count && thumbs[i] != null)
                    GUI.DrawTexture(fr, thumbs[i], ScaleMode.StretchToFill, true);

                GUI.Label(new Rect(x - 4, fr.yMax + 4f, kw + 8, 18), Baslikla(isimler[i]), sec ? sOrtaSecili : sOrtaKucuk);

                // eklenen resimleri silme (x)
                if (!HazirKat(kategoriler[i]))
                {
                    bool bekliyor = silBekleyen == i && Time.time - silZaman < 4f;
                    if (bekliyor)
                    {
                        GUI.Box(fr, GUIContent.none, sKaraPerde);
                        GUI.Label(fr, Dil("Delete?"), sOrtaBeyaz);
                    }
                    Rect xr = new Rect(fr.xMax - 20f, fr.y + 2f, 18f, 18f);
                    GUI.Box(xr, GUIContent.none, sMiniKirmizi);
                    GUI.Label(xr, "x", sOrtaBeyazKucuk);
                    if (GUI.Button(new Rect(xr.x - 4f, xr.y - 4f, xr.width + 8f, xr.height + 8f), GUIContent.none, sBos))
                    {
                        if (bekliyor) { silBekleyen = -1; ResimSil(i); return cy; }
                        silBekleyen = i; silZaman = Time.time;
                    }
                    else if (bekliyor && GUI.Button(fr, GUIContent.none, sBos)) { silBekleyen = -1; ResimSil(i); return cy; }
                }

                if (GUI.Button(new Rect(x, ty, kw, fr.height + 22f), GUIContent.none, sBos)) { silBekleyen = -1; Sec(i); }
            }

            int satir = (adet + 2) / 3;
            cy += satir * 100f + 2f;

            bool ekleVar = !HazirKat(aktifKat) && !araniyor;
            float bw = ekleVar ? (iw - 8f) / 2f : iw;
            if (ekleVar)
            {
                Rect ab = new Rect(0, cy, bw, 42f);
                GUI.Box(ab, GUIContent.none, sTabOn);
                GUI.Label(ab, Dil("+ Add image"), sOrtaBeyaz);
                if (Tikla(ab)) resimEkle = true;
            }

            Rect rb = new Rect(ekleVar ? bw + 8f : 0f, cy, bw, 42f);
            GUI.Box(rb, GUIContent.none, sKutu);
            GUI.Label(rb, Dil("Original flag"), sOrta);
            if (Tikla(rb)) Orijinale();
            cy += 50f;

            if (durum != "")
            {
                GUI.Label(new Rect(4, cy, iw - 8, 36), durum, sAltSatir);
                cy += 40f;
            }

            return cy;
        }

        // ---------- Pole (bayrak sapi) ----------
        private const int SapOzel = 15;
        private static readonly string[] SapAdlari = { "Original", "Light wood", "Walnut", "Mahogany", "Silver", "Gold", "Bronze", "Black metal",
            "White", "Black", "Red", "Blue", "Candy cane", "Rainbow", "Two-tone", "Custom PNG",
            "Birch", "Ebony", "Bamboo", "Pine", "Copper", "Rose gold", "Titanium", "Rusty iron",
            "Green", "Yellow", "Orange", "Pink", "Purple", "Cyan",
            "Checkered", "Zebra", "Camo", "Sunset", "Galaxy", "Blue spiral" };
        private const int SapSon = 35;
        private static readonly string[] SapGrup = { "WOOD", "METAL", "COLORS", "PATTERNS" };
        private int sapNo = 0;
        private BepInEx.Configuration.ConfigEntry<int> sapCfg;
        private Texture2D sapOzelTex;
        private int sapOzelSurum;
        private bool sapDosyaModu;
        private readonly Dictionary<int, Texture2D> sapIkon = new Dictionary<int, Texture2D>();

        // Private kopyanin malzemesi baska bir atlastan kopyalandigi icin ona her zaman kendi dokumuzu veriyoruz
        private bool Etkin { get { return secili >= 0 || sapNo != 0 || gizli; } }

        private static Color32 K(int r, int g, int b) { return new Color32((byte)r, (byte)g, (byte)b, 255); }
        private static Color32 Karis(Color32 a, Color32 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Color32((byte)(a.r + (b.r - a.r) * t), (byte)(a.g + (b.g - a.g) * t), (byte)(a.b + (b.b - a.b) * t), 255);
        }
        private static float Gurultu(float x, float y)
        {
            float h = Mathf.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
            return h - Mathf.Floor(h);
        }

        // u: sapin cevresi (0-1), t: sapin boyu (0 alt, 1 ust)
        private Color32 SapRenk(int no, float u, float t)
        {
            switch (no)
            {
                case 1: return Ahsap(K(226, 192, 140), K(170, 128, 80), u, t);
                case 2: return Ahsap(K(110, 70, 40), K(55, 32, 16), u, t);
                case 3: return Ahsap(K(150, 60, 40), K(85, 28, 16), u, t);
                case 4: return Metal(K(245, 246, 252), K(115, 120, 130), u);
                case 5: return Metal(K(255, 232, 140), K(170, 118, 22), u);
                case 6: return Metal(K(235, 170, 110), K(120, 70, 30), u);
                case 7: return Metal(K(120, 120, 130), K(18, 18, 22), u);
                case 8: return K(235, 235, 235);
                case 9: return K(40, 40, 45);
                case 10: return K(210, 40, 45);
                case 11: return K(45, 90, 220);
                case 12: { float f = t * 14f + u; return (f - Mathf.Floor(f)) < 0.5f ? K(240, 240, 240) : K(215, 30, 40); }
                case 13:
                {
                    Color32[] r = { K(117, 7, 135), K(0, 77, 255), K(0, 128, 38), K(255, 237, 0), K(255, 140, 0), K(228, 3, 3) };
                    return r[Mathf.Clamp((int)(t * 6f), 0, 5)];
                }
                case 14: return t >= 0.5f ? K(45, 90, 220) : K(240, 200, 40);
                case 16: return Gurultu(Mathf.Floor(u * 6f), Mathf.Floor(t * 22f)) > 0.86f ? K(55, 50, 45) : Ahsap(K(236, 228, 210), K(200, 190, 170), u, t);
                case 17: return Ahsap(K(62, 42, 32), K(24, 15, 10), u, t);
                case 18:
                {
                    float f = t * 6f; f -= Mathf.Floor(f);
                    if (f < 0.05f) return K(135, 125, 55);
                    return Karis(K(205, 195, 115), K(165, 160, 80), (Mathf.Sin(u * 20f) * 0.5f + 0.5f) * 0.35f);
                }
                case 19: return Ahsap(K(238, 205, 135), K(190, 140, 70), u, t);
                case 20: return Metal(K(255, 182, 132), K(140, 70, 40), u);
                case 21: return Metal(K(255, 212, 202), K(180, 110, 100), u);
                case 22: return Metal(K(205, 214, 228), K(70, 80, 95), u);
                case 23: return Karis(K(90, 85, 80), K(155, 72, 30), Gurultu(Mathf.Floor(u * 10f), Mathf.Floor(t * 60f)));
                case 24: return K(40, 170, 70);
                case 25: return K(245, 205, 40);
                case 26: return K(245, 130, 30);
                case 27: return K(245, 120, 180);
                case 28: return K(130, 60, 200);
                case 29: return K(40, 200, 220);
                case 30: return (((int)(u * 4f) + (int)(t * 24f)) % 2 == 0) ? K(240, 240, 240) : K(30, 30, 30);
                case 31: return Mathf.Sin(t * 40f + Mathf.Sin(u * 6f) * 1.5f) > 0.2f ? K(240, 240, 240) : K(25, 25, 25);
                case 32:
                {
                    float n = Gurultu(Mathf.Floor(u * 3f), Mathf.Floor(t * 16f));
                    return n < 0.33f ? K(85, 107, 47) : (n < 0.66f ? K(120, 100, 60) : K(50, 70, 40));
                }
                case 33: return t < 0.5f ? Karis(K(130, 60, 200), K(255, 90, 80), t * 2f) : Karis(K(255, 90, 80), K(255, 200, 60), (t - 0.5f) * 2f);
                case 34:
                {
                    if (Gurultu(Mathf.Floor(u * 12f), Mathf.Floor(t * 120f)) > 0.965f) return K(255, 255, 255);
                    return Karis(K(18, 12, 48), K(110, 40, 160), Mathf.Sin(t * 7f + u * 3f) * 0.5f + 0.5f);
                }
                case 35: { float f = t * 14f + u; return (f - Mathf.Floor(f)) < 0.5f ? K(240, 240, 240) : K(40, 90, 220); }
                case SapOzel:
                    if (sapOzelTex != null) { Color c = sapOzelTex.GetPixelBilinear(u, t); return new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), 255); }
                    return K(196, 150, 98);
            }
            return K(196, 150, 98);
        }

        private static Color32 Ahsap(Color32 acik, Color32 koyu, float u, float t)
        {
            float lif = Mathf.Sin(u * 38f + Mathf.Sin(t * 9f + u * 6f) * 1.6f) * 0.5f + 0.5f;
            float n = Gurultu(Mathf.Floor(u * 24f), Mathf.Floor(t * 90f));
            return Karis(acik, koyu, lif * 0.45f + n * 0.15f);
        }

        private static Color32 Metal(Color32 parlak, Color32 koyu, float u)
        {
            float k = 0.5f + 0.5f * Mathf.Cos((u - 0.3f) * Mathf.PI * 2f);
            return Karis(koyu, parlak, k);
        }

        // Atlas diliminde sapin bolgesi: govde u 0.058-0.148 / v 0.061-0.937, uclar u 0.20-0.24 / v 0.70-0.81
        private void SapBoya(Color32[] px)
        {
            if (sapNo == 0 || px == null || px.Length != 256 * 256) return;
            for (int y = 0; y < 256; y++)
            {
                float t = Mathf.Clamp01((y + 0.5f - 15.6f) / (239.9f - 15.6f));
                for (int x = 0; x < 45; x++)
                {
                    float u = Mathf.Clamp01((x + 0.5f - 14.9f) / (38.0f - 14.9f));
                    px[y * 256 + x] = SapRenk(sapNo, u, t);
                }
                // sapin uclari (kapaklar)
                for (int x = 45; x < 67; x++) px[y * 256 + x] = SapRenk(sapNo, 0.5f, 0.98f);
            }
        }

        private Texture2D SapIkon(int no)
        {
            Texture2D t;
            if (sapIkon.TryGetValue(no, out t) && t != null) return t;
            t = new Texture2D(12, 44, TextureFormat.RGBA32, false);
            Color32[] p = new Color32[12 * 44];
            for (int y = 0; y < 44; y++)
                for (int x = 0; x < 12; x++)
                {
                    float u = (x + 0.5f) / 12f;
                    Color32 c = no == 0 ? Ahsap(K(205, 160, 105), K(140, 95, 50), u, y / 43f) : SapRenk(no, u, y / 43f);
                    float s = 0.6f + 0.55f * Mathf.Sin(Mathf.PI * u);
                    c = new Color32((byte)Mathf.Min(255f, c.r * s), (byte)Mathf.Min(255f, c.g * s), (byte)Mathf.Min(255f, c.b * s), 255);
                    float dy = y < 6 ? 6 - y : (y > 37 ? y - 37 : 0);
                    float dx = Mathf.Abs(x - 5.5f);
                    if (dy > 0 && dx * dx + dy * dy > 36f) c.a = 0;
                    p[y * 12 + x] = c;
                }
            t.SetPixels32(p);
            t.Apply(false, false);
            t.filterMode = FilterMode.Bilinear;
            sapIkon[no] = t;
            return t;
        }

        private void SapSec(int no)
        {
            if (no == SapOzel && sapOzelTex == null) { sapDosyaModu = true; resimEkle = true; return; }
            sapNo = no;
            if (sapCfg != null) sapCfg.Value = no;
            foreach (var kv in kayitlar) kv.Value.uygulanan = "";
            if (Etkin) Sec(secili);
            else foreach (var kv in kayitlar) Geri(kv.Value);
        }

        private float sapSilZaman = -10f;

        private void SapOzelSil()
        {
            try { if (File.Exists(SapDosyasi())) File.Delete(SapDosyasi()); }
            catch (Exception e) { Logger.LogWarning("Pole image delete error: " + e.Message); }
            if (sapOzelTex != null) Destroy(sapOzelTex);
            sapOzelTex = null;
            sapIkon.Remove(SapOzel);
            sapOzelSurum++;
            if (sapNo == SapOzel) SapSec(0);
            Logger.LogInfo("Custom pole image deleted.");
        }

        private string SapDosyasi() { return Path.Combine(Path.GetDirectoryName(Info.Location), "GorillaFlag_pole.png"); }

        private void SapResimYukle(string yol, bool kopyala)
        {
            try
            {
                Type tip = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                var yukle = tip.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]) });
                byte[] veri = File.ReadAllBytes(yol);
                Texture2D tx = new Texture2D(2, 2);
                yukle.Invoke(null, new object[] { tx, veri });
                if (kopyala) File.WriteAllBytes(SapDosyasi(), veri);
                if (sapOzelTex != null) Destroy(sapOzelTex);
                sapOzelTex = tx;
                sapOzelSurum++;
                sapIkon.Remove(SapOzel);
                durum = "";
                if (kopyala) SapSec(SapOzel);
            }
            catch (Exception e) { durum = Dil("Could not add image: ") + e.Message; Logger.LogWarning("Pole image error: " + e.Message); }
        }

        private float SekmeStil(float iw)
        {
            float cy = 0f;
            int[][] gruplar = {
                new int[] { 0, 1, 2, 3, 16, 17, 18, 19 },
                new int[] { 4, 5, 6, 7, 20, 21, 22, 23 },
                new int[] { 8, 9, 10, 11, 24, 25, 26, 27, 28, 29 },
                new int[] { 12, 13, 14, 30, 31, 32, 33, 34, 35, SapOzel } };
            float bw = (iw - 3 * 6f) / 4f;
            for (int g = 0; g < gruplar.Length; g++)
            {
                EtiketYaz(Dil(SapGrup[g]), cy, iw);
                cy += 24f;
                for (int i = 0; i < gruplar[g].Length; i++)
                {
                    int no = gruplar[g][i];
                    Rect r = new Rect((i % 4) * (bw + 6f), cy + (i / 4) * 76f, bw, 70f);
                    bool on = no == sapNo;
                    GUI.Box(r, GUIContent.none, on ? sTabOn : sKutu);
                    if (no == SapOzel && sapOzelTex == null)
                        GUI.Label(new Rect(r.x, r.y + 6, r.width, 44), "+", on ? sOrtaBeyaz : sOrta);
                    else
                        GUI.DrawTexture(new Rect(r.x + r.width / 2f - 6f, r.y + 7f, 12f, 44f), SapIkon(no));
                    GUI.Label(new Rect(r.x, r.y + 50f, r.width, 18f), Dil(SapAdlari[no]), on ? sOrtaBeyaz : sOrtaKucuk);
                    if (Tikla(r))
                    {
                        if (no == SapOzel && on) { sapDosyaModu = true; resimEkle = true; }   // ozel secili iken: yeni resim sec
                        else SapSec(no);
                    }
                }
                cy += ((gruplar[g].Length + 3) / 4) * 76f + 2f;
            }
            GUI.Label(new Rect(4, cy, iw - 8, 34), Dil("Changes the color of the flag pole. Only the pole changes, the flag stays the same."), sAltSatir);
            cy += 40f;
            if (Eylem(cy, iw, "X", Dil("Original pole"), Dil("Back to the default wood pole"), false)) SapSec(0);
            cy += 62f;
            if (sapOzelTex != null)
            {
                bool bekle = Time.time - sapSilZaman < 3f;
                if (Eylem(cy, iw, "D", bekle ? Dil("Click again to delete") : Dil("Delete custom pole"), Dil("Removes the image you added for the pole"), true))
                {
                    if (bekle) { SapOzelSil(); sapSilZaman = -10f; }
                    else sapSilZaman = Time.time;
                }
                cy += 62f;
            }
            if (durum != "")
            {
                GUI.Label(new Rect(4, cy, iw - 8, 36), durum, sAltSatir);
                cy += 40f;
            }
            return cy;
        }

        // Dil, tema ve bayrak yuzleri (Settings sekmesinde)
        private float StilBolumleri(float cy0, float iw)
        {
            float cy = cy0;
            EtiketYaz(Dil("LANGUAGE"), cy, iw);
            cy += 26f;

            Rect dr = new Rect(0, cy, iw, 42f);
            GUI.Box(dr, GUIContent.none, sTrack);
            float dw = (iw - 8f) / DilAdlari.Length;
            for (int i = 0; i < DilAdlari.Length; i++)
            {
                Rect t = new Rect(4f + i * dw, cy + 4f, dw, 34f);
                if (i == dil) GUI.Box(t, GUIContent.none, sTabOn);
                GUI.Label(t, DilAdlari[i], i == dil ? sOrtaBeyaz : sOrtaSoluk);
                if (GUI.Button(t, GUIContent.none, sBos))
                {
                    dil = i;
                    if (dilCfg != null) dilCfg.Value = i;
                }
            }
            cy += 54f;

            EtiketYaz(Dil("THEME"), cy, iw);
            cy += 26f;

            for (int i = 0; i < Temalar.Length; i++)
            {
                Tema t = Temalar[i];
                float x = (i % 7) * 42f;
                float y = cy + (i / 7) * 42f;
                Rect sr = new Rect(x, y, 32f, 32f);

                Color eski = GUI.color;
                if (i == temaNo)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(sr.x - 3, sr.y - 3, 38, 38), tDaire);
                }
                GUI.color = t.vurgu;
                GUI.DrawTexture(sr, tDaire);
                Color pc = t.panel; pc.a = 1f;
                GUI.color = pc;
                GUI.DrawTexture(new Rect(sr.x + 8, sr.y + 8, 16, 16), tDaire);
                GUI.color = eski;

                if (GUI.Button(sr, GUIContent.none, sBos))
                {
                    temaNo = i;
                    if (temaCfg != null) temaCfg.Value = i;
                    stilGecersiz = true;
                }
            }

            cy += ((Temalar.Length + 6) / 7) * 42f + 8f;

            EtiketYaz(Dil("FLAG SIDES"), cy, iw);
            cy += 24f;

            if (Anahtar(cy, iw, Dil("Side 1: mirror"), ustX)) { ustX = !ustX; Yenile(); }
            cy += 38f;
            if (Anahtar(cy, iw, Dil("Side 1: upside down"), ustY)) { ustY = !ustY; Yenile(); }
            cy += 38f;
            if (Anahtar(cy, iw, Dil("Side 2: mirror"), altX)) { altX = !altX; Yenile(); }
            cy += 38f;
            if (Anahtar(cy, iw, Dil("Side 2: upside down"), altY)) { altY = !altY; Yenile(); }
            cy += 42f;

            return cy;
        }

        // Variant butonu (General sekmesinin basi)
        private float VariantCiz(float cy, float iw)
        {
            EtiketYaz(Dil("VARIANT"), cy, iw);
            cy += 26f;
            {
                Rect r = new Rect(0, cy, iw, 40f);
                bool acik = kozmetik != 3;
                SecKutu(r, acik ? sTabOn : sKutu);
                GUIStyle ys = acik ? sOrtaBeyaz : sOrta;
                GUI.Label(r, Dil(KozAdlari[kozmetik]), ys);
                GUI.Label(new Rect(r.x + 8, r.y, 20, r.height), "<", ys);
                GUI.Label(new Rect(r.xMax - 28, r.y, 20, r.height), ">", ys);
                if (Tikla(r))
                {
                    int sira = Array.IndexOf(KozSira, kozmetik);
                    KozmetikSec(KozSira[(sira + 1) % KozSira.Length]);
                }
                cy += 46f;

                // hangi sirada oldugunu gosteren noktalar
                int aktif = Array.IndexOf(KozSira, kozmetik);
                float nx = iw / 2f - (KozSira.Length * 12f - 6f) / 2f;
                Color eskiR = GUI.color;
                for (int i = 0; i < KozSira.Length; i++)
                {
                    Color nk = i == aktif ? Temalar[temaNo].vurgu : new Color(0.4f, 0.4f, 0.45f, 1f);
                    nk.a *= eskiR.a;
                    GUI.color = nk;
                    GUI.DrawTexture(new Rect(nx + i * 12f, cy, 6f, 6f), tDaire);
                }
                GUI.color = eskiR;
                cy += 12f;
            }
            GUI.Label(new Rect(4, cy - 2, iw - 8, 34), kozmetik == 3
                ? Dil("The real flag cosmetics in game are not changed.")
                : Dil("Everyone sees it. You must own and wear this flag in game."), sAltSatir);
            cy += 40f;

            return cy;
        }

        private float SekmeDonus(float iw)
        {
            float cy = 0f;
            // en ustteki dugme bu menunun ac/kapa dugmesi; kapaliyken alttaki her sey gri ve kullanilamaz
            if (Anahtar(cy, iw, Dil(gizli ? "On" : "Off"), gizli)) GizliSec(!gizli);
            cy += 38f;
            GUI.Label(new Rect(4, cy - 2, iw - 8, 34), Dil("Only you see it. You don't need to own the flag."), sAltSatir);
            cy += 40f;

            bool kapali = !gizli;
            Color soluk = new Color(1f, 1f, 1f, 0.35f);
            if (kapali) { GUI.enabled = false; GUI.color = soluk; }

            EtiketYaz(Dil("SIDE"), cy, iw);
            cy += 26f;
            {
                Rect tr2 = new Rect(0, cy, iw, 42f);
                GUI.Box(tr2, GUIContent.none, sTrack);
                string[] ta = { "Left", "Right" };
                int[] deger = { 1, 0 };
                float tw2 = (iw - 8f) / 2f;
                int simdi = tarafCfg != null ? tarafCfg.Value : 0;
                bool aktifT = gizli;

                if (!aktifT) GUI.color = new Color(1f, 1f, 1f, 0.35f);   // devre disi: soluk
                for (int i = 0; i < 2; i++)
                {
                    Rect t = new Rect(4f + i * tw2, cy + 4f, tw2, 34f);
                    bool on = simdi == deger[i];
                    if (on) SecKutu(t, sTabOn);
                    GUI.Label(t, Dil(ta[i]), on ? sOrtaBeyaz : sOrtaSoluk);
                    if (GUI.Button(t, GUIContent.none, sBos) && tarafCfg != null && aktifT)
                    {
                        tarafCfg.Value = deger[i];
                        sirtSol = deger[i] == 1;
                        if (!tutuluyor) SirtaKoy();
                    }
                }
            }
            GUI.color = kapali ? soluk : Color.white;
            cy += 48f;
            string tAcik = gizli ? Dil("Which shoulder the flag sits on. Only for Private.")
                : kozmetik == 3 ? Dil("Turn on Private at the top to choose a side.")
                : Dil("The real flag is on: change the shoulder from the wardrobe in game.");
            GUI.Label(new Rect(4, cy - 2, iw - 8, 34), tAcik, sAltSatir);
            cy += 40f;

            EtiketYaz(Dil("ROTATION"), cy, iw);
            cy += 28f;

            DialGuncelle(donus);
            Rect dr = new Rect(2, cy, 74, 74);
            GUI.DrawTexture(dr, dialTex);
            GUI.Label(dr, donus + "°", sOrtaBuyuk);

            GUI.Label(new Rect(94, cy + 2, iw - 94, 18), Dil("Turn the flag around its pole"), sAltSatir);
            Rect sl = new Rect(94, cy + 24, iw - 100, 20);
            int yeni = Kaydirici(sl, donus, 0, 360);
            if (yeni != donus) { donus = yeni; donusDirty = true; }

            string[] tick = { "0", "90", "180", "270", "360" };
            for (int i = 0; i < tick.Length; i++)
            {
                float tx = sl.x + sl.width * (i / 4f);
                GUI.Label(new Rect(tx - 16, cy + 48, 32, 16), tick[i], sOrtaKucuk);
            }
            cy += 90f;

            EtiketYaz(Dil("QUICK ANGLES"), cy, iw);
            cy += 26f;

            int[] preset = { 0, 90, 180, 270 };
            float bw = (iw - 3 * 8f) / 4f;
            for (int i = 0; i < preset.Length; i++)
            {
                Rect pr = new Rect(i * (bw + 8f), cy, bw, 38f);
                bool aktif = donus == preset[i];
                SecKutu(pr, aktif ? sTabOn : sKutu);
                GUI.Label(pr, preset[i] + "°", aktif ? sOrtaBeyaz : sOrta);
                if (Tikla(pr)) { donus = preset[i]; donusDirty = true; }
            }
            cy += 50f;

            cy = VariantCiz(cy, iw);

            GUI.enabled = true;
            GUI.color = Color.white;


            if (durum != "")
            {
                GUI.Label(new Rect(4, cy, iw - 8, 36), durum, sAltSatir);
                cy += 40f;
            }

            return cy;
        }

        private bool Eylem(float cy, float iw, string simge, string baslik, string alt, bool tehlike)
        {
            Rect r = new Rect(0, cy, iw, 54f);
            GUI.Box(r, GUIContent.none, sKutu);
            bool tik = Tikla(r);

            Rect ik = new Rect(r.x + 10, r.y + 11, 32, 32);
            GUI.Box(ik, GUIContent.none, sIkonKutu);
            GUI.Label(ik, simge, sOrtaBeyaz);

            GUIStyle bs = sBaslikSatir;
            GUI.Label(new Rect(r.x + 54, r.y + 8, r.width - 90, 20), baslik, bs);
            GUI.Label(new Rect(r.x + 54, r.y + 29, r.width - 90, 18), alt, sAltSatir);
            GUI.Label(new Rect(r.xMax - 28, r.y + 16, 20, 22), ">", sOrtaSoluk);
            return tik;
        }

        private float SekmeAraclar(float iw)
        {
            float cy = 0f;
            cy = StilBolumleri(cy, iw);

            EtiketYaz(Dil("TEXTURE QUALITY"), cy, iw);
            cy += 26f;
            {
                Rect kr = new Rect(0, cy, iw, 42f);
                GUI.Box(kr, GUIContent.none, sTrack);
                float kw2 = (iw - 8f) / KaliteAdlari.Length;
                for (int i = 0; i < KaliteAdlari.Length; i++)
                {
                    Rect t = new Rect(4f + i * kw2, cy + 4f, kw2, 34f);
                    if (i == kalite) GUI.Box(t, GUIContent.none, sTabOn);
                    GUI.Label(t, Dil(KaliteAdlari[i]), i == kalite ? sOrtaBeyaz : sOrtaSoluk);
                    if (GUI.Button(t, GUIContent.none, sBos)) KaliteSec(i);
                }
            }
            cy += 48f;
            GUI.Label(new Rect(4, cy - 2, iw - 8, 34), Dil("Sharper flag image. Normal is the game's original quality."), sAltSatir);
            cy += 40f;

            EtiketYaz(Dil("FLAG DETAIL"), cy, iw);
            cy += 26f;
            {
                Rect dr = new Rect(0, cy, iw, 42f);
                GUI.Box(dr, GUIContent.none, sTrack);
                float dw = (iw - 8f) / DetayDegerleri.Length;
                for (int i = 0; i < DetayDegerleri.Length; i++)
                {
                    int d = DetayDegerleri[i];
                    Rect t = new Rect(4f + i * dw, cy + 4f, dw, 34f);
                    if (d == detay) GUI.Box(t, GUIContent.none, sTabOn);
                    GUI.Label(t, d.ToString(), d == detay ? sOrtaBeyaz : sOrtaSoluk);
                    if (GUI.Button(t, GUIContent.none, sBos)) DetaySec(d);
                }
            }
            cy += 48f;
            GUI.Label(new Rect(4, cy - 2, iw - 8, 34), Dil("More parts = smoother waves. Only you see the difference."), sAltSatir);
            cy += 40f;

            EtiketYaz(Dil("PLANT FLAG"), cy, iw);
            cy += 26f;
            if (Anahtar(cy, iw, Dil(saplamaAcik ? "On" : "Off"), saplamaAcik)) SaplamaSec(!saplamaAcik);
            cy += 38f;
            GUI.Label(new Rect(4, cy - 2, iw - 8, 34), Dil("Hold the plant button and hit the ground or a wall with the pole. Only you see it."), sAltSatir);
            cy += 40f;

            EtiketYaz(Dil("PLANT STRENGTH"), cy, iw);
            cy += 26f;
            {
                Rect hr = new Rect(0, cy, iw, 42f);
                GUI.Box(hr, GUIContent.none, sTrack);
                float hw = (iw - 8f) / SaplamaHizlari.Length;
                for (int i = 0; i < SaplamaHizlari.Length; i++)
                {
                    Rect t = new Rect(4f + i * hw, cy + 4f, hw, 34f);
                    bool on = SaplamaHizi == SaplamaHizlari[i];
                    if (on) GUI.Box(t, GUIContent.none, sTabOn);
                    GUI.Label(t, Dil(SaplamaHizAd[i]), on ? sOrtaBeyaz : sOrtaSoluk);
                    if (GUI.Button(t, GUIContent.none, sBos)) SaplamaHiziSec(SaplamaHizlari[i]);
                }
            }
            cy += 48f;
            GUI.Label(new Rect(4, cy - 2, iw - 8, 34), Dil("How hard you must hit the ground to plant the flag. Normal is the original."), sAltSatir);
            cy += 40f;

            EtiketYaz(Dil("SHARING"), cy, iw);
            cy += 26f;
            if (Anahtar(cy, iw, Dil("Others can take my flag"), alinabilir)) AlinabilirSec(!alinabilir);
            cy += 38f;
            GUI.Label(new Rect(4, cy - 2, iw - 8, 34), Dil("In private rooms, players with this mod can pick up your planted flag, carry it, drop it or plant it again."), sAltSatir);
            cy += 40f;

            EtiketYaz(Dil("RETURN TO BACK AFTER"), cy, iw);
            cy += 26f;
            {
                Rect mr = new Rect(0, cy, iw, 42f);
                GUI.Box(mr, GUIContent.none, sTrack);
                float mw = (iw - 8f) / DonusMesafeleri.Length;
                for (int i = 0; i < DonusMesafeleri.Length; i++)
                {
                    Rect t = new Rect(4f + i * mw, cy + 4f, mw, 34f);
                    bool on = donusMesafe == DonusMesafeleri[i];
                    if (on) GUI.Box(t, GUIContent.none, sTabOn);
                    GUI.Label(t, Dil(DonusMesafeAd[i]), on ? sOrtaBeyaz : sOrtaSoluk);
                    if (GUI.Button(t, GUIContent.none, sBos)) DonusMesafeSec(DonusMesafeleri[i]);
                }
            }
            cy += 48f;
            GUI.Label(new Rect(4, cy - 2, iw - 8, 34), Dil("Your planted or dropped flag goes back to your back when you get this far from it."), sAltSatir);
            cy += 40f;

            EtiketYaz(Dil("KEYBINDS"), cy, iw);
            cy += 26f;
            {
                // menu ac/kapa (klavye)
                GUI.Label(new Rect(6, cy, iw - 130, 36), Dil("Open / close menu"), sYazi);
                Rect kr = new Rect(iw - 120, cy, 120, 36);
                GUI.Box(kr, GUIContent.none, tusDinle ? sTabOn : sKutu);
                GUI.Label(kr, tusDinle ? Dil("Press a key...") : TusAdi(menuTus), tusDinle ? sOrtaBeyaz : sOrta);
                if (Tikla(kr)) { if (tusDinle) tusDinle = false; else TusDinleBasla(); }
                cy += 42f;

                // bayragi saplama (VR kumanda): 2 ayri tus
                for (int y = 0; y < 2; y++)
                {
                    GUI.Label(new Rect(6, cy, iw - 130, 36), Dil(y == 0 ? "Plant button 1" : "Plant button 2"), sYazi);
                    Rect sr = new Rect(iw - 120, cy, 120, 36);
                    bool dinliyor = yuvaDinle == y;
                    GUI.Box(sr, GUIContent.none, dinliyor ? sTabOn : sKutu);
                    GUI.Label(sr, dinliyor ? Dil("Press a button...") : KolTusAdlari[saplaYuva[y]], dinliyor ? sOrtaBeyaz : sOrta);
                    int yy = y;
                    if (Tikla(sr)) YuvaDinleBasla(yy);
                    cy += 42f;
                }
            }
            GUI.Label(new Rect(4, cy - 2, iw - 8, 34), Dil("Click a box, then press the new key or controller button. Click again to clear."), sAltSatir);
            cy += 40f;

            EtiketYaz(Dil("APPLY METHOD"), cy, iw);
            cy += 26f;

            Rect tr = new Rect(0, cy, iw, 42f);
            GUI.Box(tr, GUIContent.none, sTrack);
            string[] ad = { "Array", "Keyword", "Shader" };
            float sw = (iw - 8f) / 3f;
            for (int i = 0; i < ad.Length; i++)
            {
                Rect t = new Rect(4f + i * sw, cy + 4f, sw, 34f);
                if (i == yontem) GUI.Box(t, GUIContent.none, sTabOn);
                GUI.Label(t, ad[i], i == yontem ? sOrtaBeyaz : sOrtaSoluk);
                if (GUI.Button(t, GUIContent.none, sBos)) { yontem = i; Yenile(); }
            }
            cy += 48f;

            GUI.Label(new Rect(4, cy, iw, 18), Dil("Keep it on Array. Only change it if the flag looks broken."), sAltSatir);
            cy += 28f;

            EtiketYaz(Dil("ACTIONS"), cy, iw);
            cy += 26f;

            if (Eylem(cy, iw, "R", Dil("Reload flags"), Dil("Use after putting PNGs in the Flags folder yourself"), false)) { PngleriYukle(); Yenile(); }
            cy += 62f;
            if (Eylem(cy, iw, "X", Dil("Reset all settings"), Dil("Everything back to default"), true)) HepsiniSifirla();
            cy += 64f;

            GUI.Label(new Rect(0, cy, iw, 18), "GorillaFlag v1.0  |  " + bayraklar.Count + Dil(" flags loaded"), sOrtaKucuk);
            cy += 28f;

            if (durum != "")
            {
                GUI.Label(new Rect(4, cy, iw - 8, 36), durum, sAltSatir);
                cy += 40f;
            }

            return cy;
        }

        private void KlasoruAc()
        {
            try
            {
                string yol = Path.Combine(Path.GetDirectoryName(Info.Location), "Flags");
                Directory.CreateDirectory(yol);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + yol + "\"");
            }
            catch (Exception e)
            {
                durum = "Could not open folder: " + e.Message;
            }
        }

        private void HepsiniSifirla()
        {
            tusDinle = false;
            menuTus = 0x46;
            if (menuTusCfg != null) menuTusCfg.Value = 0x46;
            saplaTus = 0;
            if (saplaTusCfg != null) saplaTusCfg.Value = 0;
            yuvaDinle = -1;
            AlinabilirSec(true);
            TirmanSec(true);
            SandboxSec(true);
            FirlatSec(true);
            SaplamaHiziSec(1.5f);
            BoySifirla();
            YaziStiliSec(0);
            DonusMesafeSec(60f);
            saplaYuva[0] = 1; saplaYuva[1] = 3;
            for (int y = 0; y < 2; y++) if (saplaYuvaCfg[y] != null) saplaYuvaCfg[y].Value = saplaYuva[y];
            SaplamaSec(true);
            ustX = true; ustY = false; altX = false; altY = false;
            yontem = 0;
            donus = 0;
            donusDirty = true;
            temaNo = 3;
            if (temaCfg != null) temaCfg.Value = 3;
            stilGecersiz = true;
            // varsayilanlar: orijinal bayrak, Variant None, detay 6, orijinal sap, Ingilizce, sag omuz
            secili = -1;
            SekilGuncelle();
            dil = 0;
            if (dilCfg != null) dilCfg.Value = 0;
            if (tarafCfg != null) tarafCfg.Value = 0;
            sirtSol = false;
            if (!tutuluyor) SirtaKoy();
            secili = -1;
            foreach (var kv in kayitlar) Geri(kv.Value);
            KozmetikSec(3);
            GizliSec(false);
            DetaySec(6);
            kalite = 1;
            if (kaliteCfg != null) kaliteCfg.Value = 1;
            sapNo = 0;
            if (sapCfg != null) sapCfg.Value = 0;
            foreach (var kv in kayitlar) kv.Value.uygulanan = "";
            if (donusCfg != null) donusCfg.Value = 0;
            durum = "";
            SecimleriKaydet();
            Yenile();
        }

        // ---------- menu ----------

        void OnGUI()
        {
            if (!menuAcik) return;

            if (stilGecersiz) { StilTemizle(); stilGecersiz = false; }
            StilHazirla();

            float W = 316f;
            float h = Mathf.Min(panelH, Screen.height - 28f);

            panelX = Mathf.Clamp(panelX, 0f, Mathf.Max(0f, Screen.width - W));
            panelY = Mathf.Clamp(panelY, 0f, Mathf.Max(0f, Screen.height - h));

            Rect panel = new Rect(panelX, panelY, W, h);
            Rect baslikAlani = new Rect(panel.x, panel.y, W, 54f);

            // "Made by DevUD" (DevUD tiklaninca profil acilir)
            float baslikW = sBaslik.CalcSize(new GUIContent("GorillaFlag")).x;
            float madeW = sMade.CalcSize(new GUIContent("Made by ")).x;
            float devW = sDevUD.CalcSize(new GUIContent("DevUD")).x;
            Rect madeR = new Rect(panel.x + 16f + 4f + baslikW + 8f, panel.y + 16f + 5f, madeW + 2f, 20f);
            Rect devR = new Rect(madeR.xMax, madeR.y, devW + 4f, 20f);

            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && baslikAlani.Contains(e.mousePosition) && !devR.Contains(e.mousePosition))
            {
                surukle = true;
                surukleFark = new Vector2(e.mousePosition.x - panelX, e.mousePosition.y - panelY);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && surukle)
            {
                panelX = e.mousePosition.x - surukleFark.x;
                panelY = e.mousePosition.y - surukleFark.y;
                e.Use();
            }
            else if (e.type == EventType.MouseUp)
            {
                surukle = false;
            }

            GUI.Box(panel, GUIContent.none, sPanel);

            float ix = panel.x + 16f;
            float iw = W - 32f;
            float y = panel.y + 16f;

            GUI.Label(new Rect(ix + 4, y - 2, 200, 30), "GorillaFlag", sBaslik);
            GUI.Label(madeR, "Made by ", sMade);
            GUI.Label(devR, "DevUD", sDevUD);
            if (GUI.Button(devR, GUIContent.none, sBos)) Application.OpenURL("https://guns.lol/devud_2612");
            GUI.Label(new Rect(panel.xMax - 16 - 70, y + 4, 70, 18), Dil("{0} to close").Replace("{0}", TusAdi(menuTus)), sSagKucuk);
            y += 40f;

            // sekmeler
            Rect tr = new Rect(ix, y, iw, 42f);
            GUI.Box(tr, GUIContent.none, sTrack);
            // gorunen sira: General, Flags, Pole, Settings (ic numaralar: 2, 0, 1, 3)
            string[] ad = { "General", "Flags", "Pole", "Settings" };
            int[] sira = { 2, 0, 1, 3 };
            float tw = (iw - 8f) / 4f;
            for (int i = 0; i < ad.Length; i++)
            {
                int k = sira[i];
                Rect t = new Rect(ix + 4f + i * tw, y + 4f, tw, 34f);
                if (k == sekme) GUI.Box(t, GUIContent.none, sTabOn);
                GUI.Label(t, Dil(ad[i]), k == sekme ? sOrtaBeyaz : sOrtaSoluk);
                if (GUI.Button(t, GUIContent.none, sBos)) { sekme = k; kaydirma = Vector2.zero; }
            }
            y += 54f;

            if (sekme == 0) y = BayrakUstu(ix, y, iw, e);
            else aramaOdak = false;

            float alanH = h - (y - panel.y) - 14f;
            Rect alan = new Rect(ix, y, iw, alanH);
            float maxK = Mathf.Max(0f, icerikH - alanH);

            // fare tekerlegi
            if (e.type == EventType.ScrollWheel && alan.Contains(e.mousePosition))
            {
                kaydirma.y += e.delta.y * 28f;
                e.Use();
            }

            // kaydirma cubugu
            bool bar = maxK > 0.5f;
            float cw = bar ? iw - 12f : iw;
            if (bar)
            {
                int sid = GUIUtility.GetControlID(FocusType.Passive);
                Rect barR = new Rect(ix + iw - 6f, y, 6f, alanH);
                float th = Mathf.Max(36f, alanH * alanH / Mathf.Max(icerikH, 1f));
                float ty = y + (alanH - th) * Mathf.Clamp01(kaydirma.y / maxK);
                Rect thumb = new Rect(barR.x, ty, 6f, th);

                switch (e.GetTypeForControl(sid))
                {
                    case EventType.MouseDown:
                        if (e.button == 0 && new Rect(barR.x - 6f, barR.y, barR.width + 12f, barR.height).Contains(e.mousePosition))
                        {
                            GUIUtility.hotControl = sid;
                            barTut = thumb.Contains(e.mousePosition) ? e.mousePosition.y - ty : th * 0.5f;
                            kaydirma.y = (e.mousePosition.y - barTut - y) / Mathf.Max(1f, alanH - th) * maxK;
                            e.Use();
                        }
                        break;
                    case EventType.MouseDrag:
                        if (GUIUtility.hotControl == sid)
                        {
                            kaydirma.y = (e.mousePosition.y - barTut - y) / Mathf.Max(1f, alanH - th) * maxK;
                            e.Use();
                        }
                        break;
                    case EventType.MouseUp:
                        if (GUIUtility.hotControl == sid) { GUIUtility.hotControl = 0; e.Use(); }
                        break;
                    case EventType.Repaint:
                        GUI.Box(barR, GUIContent.none, sInce);
                        GUI.Box(thumb, GUIContent.none, sIncePolu);
                        break;
                }
            }

            kaydirma.x = 0f;
            kaydirma.y = Mathf.Clamp(kaydirma.y, 0f, maxK);

            // gorunmeyen (kirpilmis) ogelere tiklanmasin
            EventType asil = e.type;
            bool engel = e.isMouse && GUIUtility.hotControl == 0 && !new Rect(ix, y, cw, alanH).Contains(e.mousePosition);
            if (engel) e.type = EventType.Ignore;

            GUI.BeginGroup(new Rect(ix - 4f, y, cw + 8f, alanH));
            GUI.BeginGroup(new Rect(4f, -kaydirma.y, cw, Mathf.Max(icerikH, alanH) + 8f));

            float cy;
            if (sekme == 0) cy = SekmeBayraklar(cw);
            else if (sekme == 1) cy = SekmeStil(cw);
            else if (sekme == 2) cy = SekmeDonus(cw);
            else cy = SekmeAraclar(cw);

            GUI.EndGroup();
            GUI.EndGroup();

            if (engel && e.type == EventType.Ignore) e.type = asil;

            if (e.type == EventType.Repaint || e.type == EventType.Layout)
            {
                icerikH = cy;
                panelH = (y - panel.y) + cy + 14f;
            }
        }

        // ---------- bayrak rotasyonu ----------

        private static void MeshAyarla(Renderer r, Mesh m)
        {
            SkinnedMeshRenderer sk = r as SkinnedMeshRenderer;
            if (sk != null) { sk.sharedMesh = m; return; }
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = m;
        }

        // Sap (atlasta x < 81) yerinde kalir, bayrak kismi sap ekseni etrafinda doner
        private Mesh DonukMesh(Mesh src, int deg)
        {
            try
            {
                Vector3[] v = src.vertices;
                Vector2[] uv = src.uv;
                Vector3[] n = src.normals;

                Vector3 mn = new Vector3(9999f, 9999f, 9999f), mx = new Vector3(-9999f, -9999f, -9999f);
                Vector3 toplam = Vector3.zero;
                int sap = 0;

                for (int i = 0; i < v.Length && i < uv.Length; i++)
                {
                    if (uv[i].x * 256f < 81f)
                    {
                        toplam += v[i];
                        mn = Vector3.Min(mn, v[i]);
                        mx = Vector3.Max(mx, v[i]);
                        sap++;
                    }
                }

                if (sap < 3)
                {
                    durum = Dil("Rotation: pole part not found in mesh.");
                    Logger.LogWarning("ROTATE: pole vertices not found (" + sap + ")");
                    return null;
                }

                Vector3 merkez = toplam / sap;
                Vector3 boy = mx - mn;
                Vector3 eksen = (boy.x >= boy.y && boy.x >= boy.z) ? Vector3.right : (boy.y >= boy.z ? Vector3.up : Vector3.forward);
                Quaternion q = Quaternion.AngleAxis(deg, eksen);

                int tasinan = 0;
                for (int i = 0; i < v.Length && i < uv.Length; i++)
                {
                    if (uv[i].x * 256f >= 81f)
                    {
                        v[i] = q * (v[i] - merkez) + merkez;
                        if (n != null && i < n.Length) n[i] = q * n[i];
                        tasinan++;
                    }
                }

                Mesh m = Instantiate(src);
                m.name = src.name + "_rot";
                m.vertices = v;
                if (n != null && n.Length == v.Length) m.normals = n;
                m.RecalculateBounds();

                Logger.LogInfo("ROTATE: " + deg + " deg | pole=" + sap + " cloth=" + tasinan + " axis=" + eksen + " center=" + merkez);
                durum = "";
                return m;
            }
            catch (Exception ex)
            {
                durum = Dil("Rotation failed (mesh not readable?). See log.");
                Logger.LogError("Rotate error: " + ex);
                return null;
            }
        }

        // ---------- takili bayragin donusu (kemikler uzerinden, dalga animasyonu bozulmaz) ----------
        private class KemikDurum { public bool var; public Vector3 tabanPos, sonPos; public Quaternion tabanRot, sonRot; }
        private class SkinBilgi
        {
            public Transform sapKemik;
            public Vector3 eksenLokal, pivotLokal;
            public List<Transform> ustKemikler = new List<Transform>();
            public Dictionary<Transform, KemikDurum> durum = new Dictionary<Transform, KemikDurum>();
            public bool gecersiz;
        }
        private readonly Dictionary<SkinnedMeshRenderer, SkinBilgi> skinler = new Dictionary<SkinnedMeshRenderer, SkinBilgi>();
        private float skinZaman;

        private SkinBilgi SkinAnaliz(SkinnedMeshRenderer sk)
        {
            SkinBilgi b = new SkinBilgi();
            try
            {
                Mesh m = sk.sharedMesh;
                Transform[] kemik = sk.bones;
                BoneWeight[] w = m.boneWeights;
                Vector2[] uv = m.uv;
                Vector3[] v = m.vertices;
                Matrix4x4[] bp = m.bindposes;
                if (kemik == null || kemik.Length == 0 || w == null || w.Length != v.Length) { b.gecersiz = true; return b; }

                int[] sapSay = new int[kemik.Length];
                HashSet<int> kumas = new HashSet<int>(), sap = new HashSet<int>();
                Vector3 sapMerkez = Vector3.zero, mn = Vector3.one * 1e9f, mx = Vector3.one * -1e9f;
                int sapN = 0;
                for (int i = 0; i < v.Length && i < uv.Length; i++)
                {
                    int bi = w[i].boneIndex0;
                    if (uv[i].x * 256f < 81f)
                    {
                        sap.Add(bi); sapSay[bi]++;
                        sapMerkez += v[i]; sapN++;
                        mn = Vector3.Min(mn, v[i]); mx = Vector3.Max(mx, v[i]);
                    }
                    else kumas.Add(bi);
                }
                kumas.ExceptWith(sap);
                if (sapN == 0 || kumas.Count == 0) { b.gecersiz = true; return b; }

                int sapIdx = 0;
                for (int i = 1; i < sapSay.Length; i++) if (sapSay[i] > sapSay[sapIdx]) sapIdx = i;
                b.sapKemik = kemik[sapIdx];

                Vector3 boy = mx - mn;
                Vector3 eksenMesh = (boy.x >= boy.y && boy.x >= boy.z) ? Vector3.right : (boy.y >= boy.z ? Vector3.up : Vector3.forward);
                b.pivotLokal = bp[sapIdx].MultiplyPoint3x4(sapMerkez / sapN);
                b.eksenLokal = bp[sapIdx].MultiplyVector(eksenMesh).normalized;

                HashSet<Transform> kumasT = new HashSet<Transform>();
                foreach (int i in kumas) if (kemik[i] != null) kumasT.Add(kemik[i]);
                foreach (Transform t in kumasT)
                    if (t.parent == null || !kumasT.Contains(t.parent)) b.ustKemikler.Add(t);

                Logger.LogInfo("ROTATE bones: pole=" + b.sapKemik.name + " clothRoots=" + b.ustKemikler.Count + " clothBones=" + kumasT.Count);
            }
            catch (Exception e) { b.gecersiz = true; Logger.LogWarning("Skin analysis error: " + e.Message); }
            return b;
        }

        void LateUpdate()
        {
            PerfBasla();
            try { SapliTakip(); } catch (Exception ex) { Logger.LogWarning("Follow error: " + ex.Message); }
            try { SapMenuKare(); } catch (Exception ex) { Logger.LogWarning("Pole menu error: " + ex.Message); }
            PerfIsaret("follow + GF Sandbox");
            try { TutOgren(); } catch (Exception) { }
            // 0'dan ayrilinca beklemeden hemen analiz et
            if (Time.time - skinZaman > 1.5f || (donus != 0 && skinler.Count == 0 && Time.time - skinZaman > 0.1f))
            {
                skinZaman = Time.time;
                List<SkinnedMeshRenderer> olu = new List<SkinnedMeshRenderer>();
                foreach (var kv in skinler) if (kv.Key == null) olu.Add(kv.Key);
                foreach (var k in olu) skinler.Remove(k);
                if (donus != 0)
                    foreach (Renderer r in HedeflerHizli())
                    {
                        SkinnedMeshRenderer sk = r as SkinnedMeshRenderer;
                        if (sk != null && yerelKopya != null && sk.transform.IsChildOf(yerelKopya.transform)) continue;
                        if (sk != null && !skinler.ContainsKey(sk)) skinler[sk] = SkinAnaliz(sk);
                    }
            }

            foreach (var kv in skinler)
            {
                SkinBilgi b = kv.Value;
                if (kv.Key == null || b.gecersiz || b.sapKemik == null) continue;
                float aci = DonusAcisi(kv.Key);
                Vector3 pivot = b.sapKemik.TransformPoint(b.pivotLokal);
                Vector3 eksen = b.sapKemik.TransformDirection(b.eksenLokal);

                foreach (Transform t in b.ustKemikler)
                {
                    if (t == null) continue;
                    KemikDurum d;
                    if (!b.durum.TryGetValue(t, out d)) { d = new KemikDurum(); b.durum[t] = d; }

                    // animasyon yeni bir poz yazdiysa onu taban al; yazmadiysa eski tabandan devam et (birikme olmaz)
                    if (!d.var || t.localPosition != d.sonPos || t.localRotation != d.sonRot)
                    {
                        d.tabanPos = t.localPosition;
                        d.tabanRot = t.localRotation;
                    }
                    t.localPosition = d.tabanPos;
                    t.localRotation = d.tabanRot;
                    if (aci != 0f) t.RotateAround(pivot, eksen, aci);
                    d.sonPos = t.localPosition;
                    d.sonRot = t.localRotation;
                    d.var = aci != 0f;
                }
            }
            if (donus == 0) skinler.Clear();
            PerfIsaret("cosmetic rotation");
            PerfBitir("LateUpdate");
        }

        // Bayrak sirttaysa donus uygulanmaz (kumas sapin obur tarafina, yani govdeye gecerdi); elde / yere sapliyken uygulanir
        private float DonusAcisi(SkinnedMeshRenderer sk)
        {
            if (donus == 0) return 0f;
            // kendi kopyamiz iskeletiyle doner (YerelDonusUygula); kemikleri her karede dondurmek fizikle birikip bozuyordu
            if (yerelKopya != null && sk.transform.IsChildOf(yerelKopya.transform)) return 0f;
            return donus;
        }

        // Hedefler() tum sahneyi tarar (pahali): donus icin listeyi saniyede bir yenile
        private List<Renderer> hedefOnbellek;
        private float hedefZaman = -10f;
        private int hedefKoz = -1;
        private List<Renderer> HedeflerHizli()
        {
            int kk = kozmetik * 2 + (gizli ? 1 : 0);
            if (hedefOnbellek == null || Time.time - hedefZaman > 1f || hedefKoz != kk || hedefOnbellek.Exists(r => r == null))
            {
                hedefOnbellek = Hedefler();
                hedefZaman = Time.time;
                hedefKoz = kk;
            }
            return hedefOnbellek;
        }

        private void DonusGuncelle()
        {
            if (donus == 0 && donusKayit.Count == 0) return;

            List<Renderer> olu = new List<Renderer>();
            foreach (var kv in donusKayit)
                if (kv.Key == null) olu.Add(kv.Key);
            foreach (Renderer r in olu) donusKayit.Remove(r);

            foreach (Renderer r in HedeflerHizli())
            {
                if (yerelKopya != null && r.transform.IsChildOf(yerelKopya.transform)) continue;
                if (r is SkinnedMeshRenderer) continue;   // takili bayrak: kemiklerle dondurulur (KemikDonus)
                DKayit d;
                if (!donusKayit.TryGetValue(r, out d))
                {
                    if (donus == 0) continue;
                    Mesh o = MeshAl(r);
                    if (o == null) continue;
                    d = new DKayit();
                    d.orijinal = o;
                    d.aci = 0;
                    donusKayit[r] = d;
                }

                if (d.aci == donus) continue;

                if (donus == 0)
                {
                    MeshAyarla(r, d.orijinal);
                    if (d.donuk != null) Destroy(d.donuk);
                    d.donuk = null;
                    d.aci = 0;
                    continue;
                }

                Mesh yeni = DonukMesh(d.orijinal, donus);
                d.aci = donus;
                if (yeni == null) continue;

                if (d.donuk != null) Destroy(d.donuk);
                d.donuk = yeni;
                MeshAyarla(r, yeni);
            }
        }

        // ---------- Yardimcilar ----------

        private static string MeshAdi(Renderer r)
        {
            Mesh mesh = null;
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf != null) mesh = mf.sharedMesh;
            SkinnedMeshRenderer sk = r as SkinnedMeshRenderer;
            if (sk != null) mesh = sk.sharedMesh;
            return mesh != null ? mesh.name : "";
        }

        private static Mesh MeshAl(Renderer r)
        {
            SkinnedMeshRenderer sk = r as SkinnedMeshRenderer;
            if (sk != null) return sk.sharedMesh;
            MeshFilter mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static string Yol(Transform t)
        {
            string s = t.name;
            int n = 0;
            while (t.parent != null && n < 8)
            {
                t = t.parent;
                s = t.name + "/" + s;
                n++;
            }
            return s;
        }

        // Hedefler: tum sahneyi taramak pahali; sonucu sakla, en fazla 6 sn'de bir (ya da ayar degisince) yeniden tara
        private List<Renderer> hedefTumOnbellek;
        private float hedefTumZaman = -100f;
        private string hedefTumAnahtar = "";
        private List<Renderer> Hedefler()
        {
            string an = kozmetik + "|" + gizli + "|" + (yerelKopya != null ? yerelKopya.GetInstanceID() : 0) + "|" + (yerelRgo != null ? yerelRgo.GetInstanceID() : 0);
            if (hedefTumOnbellek == null || an != hedefTumAnahtar || Time.time - hedefTumZaman > 15f)
            {
                hedefTumOnbellek = HedeflerTara();
                hedefTumAnahtar = an;
                hedefTumZaman = Time.time;
            }
            else hedefTumOnbellek.RemoveAll(r => r == null);
            return new List<Renderer>(hedefTumOnbellek);
        }

        // Obje adi, materyal adi veya mesh adi eslesen tum renderer'lar
        private readonly Dictionary<int, bool> eslesmeOnbellek = new Dictionary<int, bool>();
        private int eslesmeKoz = -1;
        private List<Renderer> HedeflerTara()
        {
            List<Renderer> sonuc = new List<Renderer>();
            if (kozmetik == 3 && !gizli) return sonuc;   // None ve Private kapali: hicbir seye dokunma
            // None + Private: sadece kendi kopyam -> sahneyi hic tarama
            if (kozmetik == 3)
            {
                if (yerelKopya != null) foreach (Renderer r in yerelKopya.GetComponentsInChildren<Renderer>(true)) sonuc.Add(r);
                return sonuc;
            }
            string anahtar = Temizle(HedefAdi);
            if (eslesmeKoz != kozmetik) { eslesmeOnbellek.Clear(); eslesmeKoz = kozmetik; }

            foreach (Renderer r in FindObjectsOfType<Renderer>())
            {
                if (uzakRendererlar.Contains(r)) continue;   // diger oyuncularin bayraklari kendi dokularini kullanir
                if (YerelMi(r)) { sonuc.Add(r); continue; }
                // her renderer'in adini bir kez incele, sonucu sakla (isim karsilastirmasi pahali)
                bool onceki;
                if (eslesmeOnbellek.TryGetValue(r.GetInstanceID(), out onceki)) { if (onceki) sonuc.Add(r); continue; }
                Material m = r.sharedMaterial;
                string on = Temizle(r.gameObject.name), mn = m != null ? Temizle(m.name) : "", me = Temizle(MeshAdi(r));
                bool eslesme;
                if (YerelMi(r)) eslesme = true;            // Private kopya
                else if (kozmetik == 3) eslesme = false;   // None: gercek kozmetiklere dokunma
                else if (kozmetik == 1)
                {
                    // Trans Pride Flag kozmetigi
                    eslesme = (on.Contains("trans") && on.Contains("flag"))
                        || (mn.Contains("trans") && mn.Contains("flag"))
                        || (me.Contains("trans") && me.Contains("flag"));
                }
                else
                {
                    eslesme = on.Contains(anahtar) || mn.Contains(anahtar) || me.Contains(anahtar);
                }

                eslesmeOnbellek[r.GetInstanceID()] = eslesme;
                if (eslesme) sonuc.Add(r);
            }
            if (eslesmeOnbellek.Count > 20000) eslesmeOnbellek.Clear();

            return sonuc;
        }

        private Kayit KayitAl(Renderer r)
        {
            Kayit k;
            if (kayitlar.TryGetValue(r, out k)) return k;

            k = new Kayit();
            k.mat = r.material;
            k.shader = k.mat.shader;
            k.keywords = k.mat.shaderKeywords;
            k.dilim = k.mat.HasProperty("_BaseMap_AtlasSlice") ? k.mat.GetFloat("_BaseMap_AtlasSlice") : 0f;

            foreach (string p in new string[] { "_BaseMap", "_BaseMap_Atlas", "_MainTex" })
                if (k.mat.HasProperty(p)) k.tex[p] = k.mat.GetTexture(p);

            kayitlar[r] = k;
            Logger.LogInfo("Found: " + Yol(r.transform) + " | shader=" + k.shader.name + " | slice=" + k.dilim);
            return k;
        }

        private void Geri(Kayit k)
        {
            if (k.mat == null) return;

            k.mat.shader = k.shader;
            k.mat.shaderKeywords = k.keywords;
            foreach (var t in k.tex) k.mat.SetTexture(t.Key, t.Value);
            foreach (var f in k.floatlar) k.mat.SetFloat(f.Key, f.Value);
            k.floatlar.Clear();
            k.uygulanan = "";
        }

        private static Texture2D Kucult(Texture src, int w, int h)
        {
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);

            RenderTexture eski = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D o = new Texture2D(w, h, TextureFormat.RGBA32, false);
            o.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            o.Apply();

            RenderTexture.active = eski;
            RenderTexture.ReleaseTemporary(rt);
            return o;
        }

        private void PngKaydet(Texture src, string ad)
        {
            Texture2D o = Kucult(src, src.width, src.height);

            Type tip = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
            var enc = tip.GetMethod("EncodeToPNG", new Type[] { typeof(Texture2D) });
            byte[] png = (byte[])enc.Invoke(null, new object[] { o });

            string klasor = Path.Combine(Path.GetDirectoryName(Info.Location), "debug");
            Directory.CreateDirectory(klasor);
            string yol = Path.Combine(klasor, ad);
            File.WriteAllBytes(yol, png);
            Logger.LogInfo("PNG saved: " + yol);
        }

        // ---------- Atlas olusturma ----------

        // Orijinal bayrak dilimini okunabilir sablon olarak al
        private bool SablonHazirla(Kayit k)
        {
            if (sablonlar.ContainsKey((int)k.dilim)) return true;

            try
            {
                Texture orig;
                k.tex.TryGetValue("_BaseMap_Atlas", out orig);
                Texture2DArray oa = orig as Texture2DArray;
                if (oa == null) return false;

                int dilim = (int)k.dilim;
                string gf = oa.graphicsFormat.ToString();
                sablonSrgb = gf.IndexOf("SRGB", StringComparison.OrdinalIgnoreCase) >= 0;
                orijinalWrap = oa.wrapMode;
                orijinalFilter = oa.filterMode;
                orijinalAniso = oa.anisoLevel;

                Logger.LogInfo("TEMPLATE: slice=" + dilim + " format=" + oa.format + " gf=" + gf
                    + " mip=" + oa.mipmapCount + " size=" + oa.width + "x" + oa.height
                    + " wrap=" + oa.wrapMode + " filter=" + oa.filterMode);

                Texture2D bc = new Texture2D(oa.width, oa.height, oa.format, false);
                Graphics.CopyTexture(oa, dilim, 0, bc, 0, 0);
                Texture2D ok = Kucult(bc, 256, 256);
                sablonlar[dilim] = ok.GetPixels32();
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError("Template error: " + e);
                return false;
            }
        }

        private static bool KahveMi(Color32 c)
        {
            float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
            float mx = Mathf.Max(r, Mathf.Max(g, b));
            float mn = Mathf.Min(r, Mathf.Min(g, b));
            float d = mx - mn;

            if (mx < 0.05f || d < 0.02f) return true;

            float h;
            if (mx == r) h = ((g - b) / d) % 6f;
            else if (mx == g) h = (b - r) / d + 2f;
            else h = (r - g) / d + 4f;

            h *= 60f;
            if (h < 0f) h += 360f;

            return h >= 15f && h <= 45f && mx < 0.62f;
        }

        // vTop: 0 = bayragin ustu, 1 = alti
        private static Color32 Ornekle(Color32[] p, int w, int h, float u, float vTop)
        {
            float fx = u * (w - 1);
            float fy = (1f - vTop) * (h - 1);

            int x0 = Mathf.Clamp((int)fx, 0, w - 1);
            int y0 = Mathf.Clamp((int)fy, 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1);
            int y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = fx - x0, ty = fy - y0;

            Color32 a = p[y0 * w + x0], b = p[y0 * w + x1], c = p[y1 * w + x0], d = p[y1 * w + x1];

            float r = Mathf.Lerp(Mathf.Lerp(a.r, b.r, tx), Mathf.Lerp(c.r, d.r, tx), ty);
            float g = Mathf.Lerp(Mathf.Lerp(a.g, b.g, tx), Mathf.Lerp(c.g, d.g, tx), ty);
            float bl = Mathf.Lerp(Mathf.Lerp(a.b, b.b, tx), Mathf.Lerp(c.b, d.b, tx), ty);

            return new Color32((byte)(r + 0.5f), (byte)(g + 0.5f), (byte)(bl + 0.5f), 255);
        }

        private static void BolgeDoldur(Color32[] px, Bolge b, Color32[] fp, int fw, int fh, bool flipX, bool flipY)
        {
            BolgeDoldur(px, 1, b, fp, fw, fh, flipX, flipY);
        }

        // q: doku kalite carpani (1 = 256, 2 = 512, 4 = 1024 piksel)
        private static void BolgeDoldur(Color32[] px, int q, Bolge b, Color32[] fp, int fw, int fh, bool flipX, bool flipY)
        {
            const int N = 3;
            int S = 256 * q;
            float gw = b.x1 - b.x0;
            float gh = b.y1 - b.y0;

            for (int yq = b.py0 * q; yq < b.py1 * q; yq++)
            {
                for (int xq = b.px0 * q; xq < b.px1 * q; xq++)
                {
                    int i = (S - 1 - yq) * S + xq;
                    float x = xq / (float)q, y = yq / (float)q;

                    // Ust bolgenin solundaki kahverengi sap parcasina dokunma
                    if (b.ust && x < b.x0 && KahveMi(px[i])) continue;

                    float r = 0f, g = 0f, bl = 0f;
                    for (int sy = 0; sy < N; sy++)
                    {
                        for (int sx = 0; sx < N; sx++)
                        {
                            float fu = Mathf.Clamp01((x + (sx + 0.5f) / (N * q) - b.x0) / gw);
                            float fv = Mathf.Clamp01((y + (sy + 0.5f) / (N * q) - b.y0) / gh);
                            if (flipX) fu = 1f - fu;
                            if (flipY) fv = 1f - fv;

                            Color32 c = Ornekle(fp, fw, fh, fu, fv);
                            r += c.r; g += c.g; bl += c.b;
                        }
                    }

                    float n = N * N;
                    px[i] = new Color32((byte)(r / n + 0.5f), (byte)(g / n + 0.5f), (byte)(bl / n + 0.5f), 255);
                }
            }
        }

        // ---------- Doku kalitesi ----------
        private static readonly string[] KaliteAdlari = { "Normal", "High", "Ultra" };
        private int kalite = 1;
        private BepInEx.Configuration.ConfigEntry<int> kaliteCfg;
        private int KaliteCarpani { get { return kalite == 2 ? 4 : (kalite == 1 ? 2 : 1); } }

        // 256'lik dokuyu q kat buyut (bilinear)
        private static Color32[] Buyut(Color32[] px, int q)
        {
            int S = 256 * q;
            Color32[] o = new Color32[S * S];
            for (int y = 0; y < S; y++)
            {
                float fy = (y + 0.5f) / q - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, 255), y1 = Mathf.Min(y0 + 1, 255);
                float ty = Mathf.Clamp01(fy - y0);
                for (int x = 0; x < S; x++)
                {
                    float fx = (x + 0.5f) / q - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, 255), x1 = Mathf.Min(x0 + 1, 255);
                    float tx = Mathf.Clamp01(fx - x0);
                    Color32 a = Color32.Lerp(px[y0 * 256 + x0], px[y0 * 256 + x1], tx);
                    Color32 b = Color32.Lerp(px[y1 * 256 + x0], px[y1 * 256 + x1], tx);
                    o[y * S + x] = Color32.Lerp(a, b, ty);
                }
            }
            return o;
        }

        private void KaliteSec(int k)
        {
            if (k == kalite) return;
            kalite = k;
            if (kaliteCfg != null) kaliteCfg.Value = k;
            foreach (var kv in kayitlar) kv.Value.uygulanan = "";
            Yenile();
        }

        // Secilen bayragi orijinal atlas yerlesimine uygun 256x256 doku haline getir
        private Texture2D AtlasAl(Kayit k, int idx)
        {
            string anahtar = (int)k.dilim + "|" + idx + "|" + Bayrakliklar();
            Texture2D a;
            if (atlaslar.TryGetValue(anahtar, out a) && a != null) return a;

            if (!SablonHazirla(k)) return idx >= 0 ? bayraklar[idx] : null;

            Color32[] px = (Color32[])sablonlar[(int)k.dilim].Clone();
            if (idx >= 0)
            {
                Texture2D flag = bayraklar[idx];
                Color32[] fp = flag.GetPixels32();
                BolgeDoldur(px, Ust, fp, flag.width, flag.height, ustX, ustY);
                BolgeDoldur(px, Alt, fp, flag.width, flag.height, altX, altY);
            }
            else if (idx == -2)
            {
                // Private'ta bayrak secili degilse: bos beyaz bayrak (LGBT bayragi yerine)
                Color32[] beyaz = new Color32[4 * 3];
                for (int i = 0; i < beyaz.Length; i++) beyaz[i] = new Color32(245, 245, 245, 255);
                BolgeDoldur(px, Ust, beyaz, 4, 3, false, false);
                BolgeDoldur(px, Alt, beyaz, 4, 3, false, false);
            }
            SapBoya(px);

            // yuksek kalite: once buyut, sonra bayragi buyuk cozunurlukte yeniden ciz
            int q = KaliteCarpani;
            if (q > 1)
            {
                px = Buyut(px, q);
                if (idx >= 0)
                {
                    Texture2D flag = bayraklar[idx];
                    Color32[] fp = flag.GetPixels32();
                    BolgeDoldur(px, q, Ust, fp, flag.width, flag.height, ustX, ustY);
                    BolgeDoldur(px, q, Alt, fp, flag.width, flag.height, altX, altY);
                }
                else if (idx == -2)
                {
                    Color32[] beyaz = new Color32[4 * 3];
                    for (int i = 0; i < beyaz.Length; i++) beyaz[i] = new Color32(245, 245, 245, 255);
                    BolgeDoldur(px, q, Ust, beyaz, 4, 3, false, false);
                    BolgeDoldur(px, q, Alt, beyaz, 4, 3, false, false);
                }
            }

            a = new Texture2D(256 * q, 256 * q, TextureFormat.RGBA32, false);
            a.SetPixels32(px);
            a.Apply(false, false);
            a.wrapMode = TextureWrapMode.Clamp;
            a.name = "atlas_" + (idx >= 0 ? isimler[idx] : "pole");
            atlaslar[anahtar] = a;

            return a;
        }

        private Texture2DArray TekDilimDizi(string anahtar, Texture2D atlas)
        {
            Texture2DArray arr;
            if (diziler.TryGetValue(anahtar, out arr) && arr != null) return arr;

            int boy = atlas.width == atlas.height && (atlas.width == 256 || atlas.width == 512 || atlas.width == 1024) ? atlas.width : 256;
            Color32[] px = (atlas.width == boy && atlas.height == boy)
                ? atlas.GetPixels32()
                : Kucult(atlas, 256, 256).GetPixels32();

            arr = new Texture2DArray(boy, boy, 1, TextureFormat.RGBA32, true, !sablonSrgb);
            arr.SetPixels32(px, 0);
            arr.Apply(true, false);
            arr.wrapMode = orijinalWrap;
            // keskin doku: yandan / uzaktan bakinca camurlasmasin (trilinear + anizotropik, biraz keskinlestirme)
            arr.filterMode = orijinalFilter == FilterMode.Point ? FilterMode.Point : FilterMode.Trilinear;
            arr.anisoLevel = Mathf.Max(orijinalAniso, 8);
            arr.mipMapBias = -0.5f;

            diziler[anahtar] = arr;
            return arr;
        }

        // ---------- Uygulama ----------

        private void Uygula(Kayit k, int idx, int yontemNo)
        {
            string kod = idx + "|" + yontemNo + "|" + Bayrakliklar();
            if (k.uygulanan == kod) return;

            Geri(k);
            Material m = k.mat;
            k.uygulanan = kod;

            Texture2D atlas = AtlasAl(k, idx);
            if (atlas == null) return;

            if (yontemNo == 0)
            {
                // Orijinal shader kalir: 1 dilimlik kendi dizimizi kullan, dilim numarasini 0 yap
                if (m.HasProperty("_BaseMap_Atlas") && m.HasProperty("_BaseMap_AtlasSlice"))
                {
                    Texture2DArray arr = TekDilimDizi((int)k.dilim + "|" + idx + "|" + Bayrakliklar(), atlas);
                    m.SetTexture("_BaseMap_Atlas", arr);

                    if (!k.floatlar.ContainsKey("_BaseMap_AtlasSlice"))
                        k.floatlar["_BaseMap_AtlasSlice"] = m.GetFloat("_BaseMap_AtlasSlice");
                    m.SetFloat("_BaseMap_AtlasSlice", 0f);
                }
                else
                {
                    Logger.LogWarning("Array method: atlas properties missing");
                }
            }
            else if (yontemNo == 1)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", atlas);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", atlas);

                m.DisableKeyword("_USE_TEX_ARRAY_ATLAS");
                m.EnableKeyword("_USE_TEXTURE");

                Shader sh = m.shader;
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    string n = sh.GetPropertyName(i);
                    if (n == "_USE_TEX_ARRAY_ATLAS")
                    {
                        if (!k.floatlar.ContainsKey(n)) k.floatlar[n] = m.GetFloat(n);
                        m.SetFloat(n, 0f);
                    }
                }
            }
            else
            {
                Shader s = Shader.Find("Universal Render Pipeline/Unlit");
                if (s == null) s = Shader.Find("Unlit/Texture");
                if (s == null) s = Shader.Find("Sprites/Default");
                if (s == null) { Logger.LogWarning("Shader method: no simple shader found"); return; }

                m.shader = s;
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", atlas);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", atlas);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
                if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            }
        }

        private int BayragiUygula()
        {
            List<Renderer> olu = new List<Renderer>();
            foreach (var kv in kayitlar)
                if (kv.Key == null) olu.Add(kv.Key);
            foreach (Renderer r in olu) kayitlar.Remove(r);

            int sayi = 0;

            foreach (Renderer r in Hedefler())
            {
                if (r.sharedMaterial == null) continue;

                Kayit k = KayitAl(r);
                Uygula(k, secili < 0 && YerelMi(r) ? -2 : secili, yontem);   // -2: Private'ta bos beyaz bayrak
                sayi++;
            }

            return sayi;
        }

        private void Sec(int i)
        {
            secili = i;
            SekilGuncelle();
            SecimleriKaydet();
            zamanlayici = Time.time;
            int n = BayragiUygula();

            if (n == 0 && kozmetik == 3 && !gizli)
            {
                durum = Dil("Private and Variant are off. Turn one on in General.");
            }
            else if (n == 0 && kozmetik == 3)
            {
                durum = "";   // Private kopya kurulunca uygulanir
            }
            else if (n == 0)
            {
                durum = Dil("Flag not found. Equip the flag cosmetic, it applies automatically.");
                Logger.LogWarning("Flag object not found.");
            }
            else
            {
                durum = "";
                Logger.LogInfo("Flag: " + (i >= 0 ? isimler[i] : "(original)") + " | method " + YontemAdlari[yontem]
                    + " | flips=" + Bayrakliklar() + " (" + n + " objects)");
            }
        }

        private void Orijinale()
        {
            foreach (var kv in kayitlar) Geri(kv.Value);

            secili = -1;
            durum = "";
            SecimleriKaydet();
            SekilGuncelle();
            Logger.LogInfo("Original flag restored.");
            if (Etkin) Sec(-1);   // sap rengi kalsin, Private bayrak orijinal dokuyla gorunsun
        }

        private void Listele()
        {
            foreach (Renderer r in FindObjectsOfType<Renderer>())
            {
                string mat = r.sharedMaterial != null ? r.sharedMaterial.name : "-";
                Logger.LogInfo(r.gameObject.name + "  |  " + mat);
            }
        }

        // ---------- Mesh dokumu (analiz icin) ----------

        private static int Bul(int[] p, int x)
        {
            while (p[x] != x)
            {
                p[x] = p[p[x]];
                x = p[x];
            }
            return x;
        }

        private static string F(float v)
        {
            return v.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        // ---------- DEBUG: gercek bayrak kozmetiginin yapisi + kemik animasyonu ----------
        private StringBuilder animKayit;
        private float animBas;
        private SkinnedMeshRenderer animSk;
        private Transform animKok;
        private Vector3 animOncekiPos;

        private static string Deger(object o)
        {
            if (o == null) return "null";
            if (o is UnityEngine.Object) { UnityEngine.Object uo = (UnityEngine.Object)o; return uo == null ? "null" : o.GetType().Name + ":" + uo.name; }
            if (o is float) return F((float)o);
            if (o is System.Collections.ICollection) return o.GetType().Name + "[" + ((System.Collections.ICollection)o).Count + "]";
            string t = o.ToString();
            return t.Length > 120 ? t.Substring(0, 120) : t;
        }

        private void DerinYaz(StringBuilder sb, string ad, object o, int derin)
        {
            const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            string bos = new string(' ', 4 + derin * 2);
            if (o == null || derin > 4 || o is UnityEngine.Object || o is string || o.GetType().IsPrimitive || o is Enum
                || o is Vector3 || o is Quaternion || o is Vector2 || o is Vector4 || o is Color)
            { sb.AppendLine(bos + ad + " = " + Deger(o)); return; }
            if (o is AnimationCurve)
            {
                AnimationCurve ac = (AnimationCurve)o;
                StringBuilder k = new StringBuilder();
                foreach (Keyframe kf in ac.keys) k.Append(" [" + F(kf.time) + "," + F(kf.value) + "," + F(kf.inTangent) + "," + F(kf.outTangent) + "]");
                sb.AppendLine(bos + ad + " = Curve" + k);
                return;
            }
            if (o is Array)
            {
                Array a = (Array)o;
                sb.AppendLine(bos + ad + " = " + o.GetType().Name + " len " + a.Length);
                int i = 0;
                foreach (object e in a) { if (i >= 12) break; DerinYaz(sb, ad + "[" + i + "]", e, derin + 1); i++; }
                return;
            }
            sb.AppendLine(bos + ad + " : " + o.GetType().FullName);
            Type ty = o.GetType();
            while (ty != null && ty != typeof(object))
            {
                foreach (var f in ty.GetFields(bf | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    object v = null;
                    try { v = f.GetValue(o); } catch (Exception) { }
                    DerinYaz(sb, f.Name, v, derin + 1);
                }
                ty = ty.BaseType;
            }
        }

        private void GercekBayrakDok()
        {
            SkinnedMeshRenderer sk = YerelKaynakBul();
            if (sk == null) { durum = "Real flag not found. Equip the Pride Flag first."; Logger.LogWarning("J dump: real flag not found"); return; }
            Transform kok = sk.transform;
            while (kok.parent != null && kok.name.IndexOf("Anchor", StringComparison.OrdinalIgnoreCase) < 0) kok = kok.parent;
            string klasor = Path.Combine(Path.GetDirectoryName(Info.Location), "debug");
            Directory.CreateDirectory(klasor);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# root=" + Yol(kok) + " active=" + sk.gameObject.activeInHierarchy);
            const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            foreach (Transform t in kok.GetComponentsInChildren<Transform>(true))
            {
                sb.AppendLine("T " + Yol(t) + " lp=" + t.localPosition.ToString("F4") + " lr=" + t.localRotation.ToString("F4") + " ls=" + t.localScale.ToString("F4"));
                foreach (Component c in t.GetComponents<Component>())
                {
                    if (c == null || c is Transform) continue;
                    sb.AppendLine("  C " + c.GetType().FullName);
                    string tn = c.GetType().Name;
                    if (tn == "Animator" || tn == "Animation")
                    {
                        // AnimationModule referansi yok: yansima ile oku
                        foreach (string pr in new string[] { "enabled", "speed", "isPlaying", "cullingMode", "updateMode" })
                        {
                            var pi = c.GetType().GetProperty(pr);
                            if (pi != null) { object v = null; try { v = pi.GetValue(c, null); } catch (Exception) { } sb.AppendLine("    " + pr + "=" + Deger(v)); }
                        }
                        var pc = c.GetType().GetProperty(tn == "Animator" ? "runtimeAnimatorController" : "clip");
                        object ctrl = pc != null ? pc.GetValue(c, null) : null;
                        sb.AppendLine("    ctrl=" + Deger(ctrl));
                        if (ctrl != null && tn == "Animator")
                        {
                            var pcl = ctrl.GetType().GetProperty("animationClips");
                            Array clips = pcl != null ? pcl.GetValue(ctrl, null) as Array : null;
                            if (clips != null)
                                foreach (object cl in clips)
                                {
                                    var pl = cl.GetType().GetProperty("length");
                                    sb.AppendLine("    clip " + Deger(cl) + " len=" + (pl != null ? Deger(pl.GetValue(cl, null)) : "?"));
                                }
                        }
                    }
                    else if (c is MonoBehaviour)
                    {
                        Type ty = c.GetType();
                        int n = 0;
                        while (ty != null && ty != typeof(MonoBehaviour) && n < 4)
                        {
                            foreach (var f in ty.GetFields(bf | System.Reflection.BindingFlags.DeclaredOnly))
                            {
                                object v = null;
                                try { v = f.GetValue(c); } catch (Exception) { }
                                sb.AppendLine("    " + ty.Name + "." + f.Name + " (" + f.FieldType.Name + ") = " + Deger(v));
                            }
                            ty = ty.BaseType; n++;
                        }
                    }
                }
            }
            // skin bilgisi
            Mesh m = sk.sharedMesh;
            sb.AppendLine("# SMR rootBone=" + (sk.rootBone != null ? Yol(sk.rootBone) : "null") + " bones=" + (sk.bones != null ? sk.bones.Length : 0) + " quality=" + sk.quality);
            if (sk.bones != null)
                for (int i = 0; i < sk.bones.Length; i++)
                    sb.AppendLine("B " + i + " " + (sk.bones[i] != null ? Yol(sk.bones[i]) : "null"));
            if (m != null)
            {
                Matrix4x4[] bp = m.bindposes;
                for (int i = 0; i < bp.Length; i++)
                {
                    Matrix4x4 b = bp[i];
                    sb.AppendLine("BP " + i + " " + F(b.m00) + " " + F(b.m01) + " " + F(b.m02) + " " + F(b.m03) + " " + F(b.m10) + " " + F(b.m11) + " " + F(b.m12) + " " + F(b.m13) + " " + F(b.m20) + " " + F(b.m21) + " " + F(b.m22) + " " + F(b.m23));
                }
                BoneWeight[] w = m.boneWeights;
                for (int i = 0; i < w.Length; i++)
                    sb.AppendLine("W " + i + " " + w[i].boneIndex0 + ":" + F(w[i].weight0) + " " + w[i].boneIndex1 + ":" + F(w[i].weight1) + " " + w[i].boneIndex2 + ":" + F(w[i].weight2) + " " + w[i].boneIndex3 + ":" + F(w[i].weight3));
            }
            File.WriteAllText(Path.Combine(klasor, "flag_structure.txt"), sb.ToString());
            StringBuilder db = new StringBuilder();
            foreach (Component c in kok.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.GetType().Namespace != "BoingKit") continue;
                db.AppendLine("# " + c.GetType().FullName + " on " + c.name);
                foreach (string fa in new string[] { "BoneChains", "Params" })
                {
                    Type ty = c.GetType(); System.Reflection.FieldInfo fi = null;
                    while (ty != null && fi == null) { fi = ty.GetField(fa, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance); ty = ty.BaseType; }
                    if (fi != null) DerinYaz(db, fa, fi.GetValue(c), 0);
                }
            }
            File.WriteAllText(Path.Combine(klasor, "flag_boing.txt"), db.ToString());

            // 10 saniye kemik animasyonu kaydi
            animSk = sk; animKok = kok; animBas = Time.time; animOncekiPos = kok.position;
            animKayit = new StringBuilder();
            animKayit.AppendLine("# t | rootWorldPos | per bone: localPos localRot");
            durum = "Recording flag animation for 10 s... stand still 5 s, then move.";
            Logger.LogInfo("J dump: structure written, recording animation");
        }

        private void AnimKayitKare()
        {
            if (animKayit == null) return;
            if (animSk == null) { animKayit = null; return; }
            float t = Time.time - animBas;
            StringBuilder sb = animKayit;
            sb.Append(F(t)).Append(" | ").Append(animKok.position.ToString("F4")).Append(" |");
            foreach (Transform b in animSk.bones)
            {
                if (b == null) { sb.Append(" null"); continue; }
                Vector3 lp = b.localPosition; Quaternion lr = b.localRotation;
                sb.Append(' ').Append(F(lp.x)).Append(',').Append(F(lp.y)).Append(',').Append(F(lp.z))
                  .Append(' ').Append(F(lr.x)).Append(',').Append(F(lr.y)).Append(',').Append(F(lr.z)).Append(',').Append(F(lr.w));
            }
            sb.AppendLine();
            if (t > 10f)
            {
                string klasor = Path.Combine(Path.GetDirectoryName(Info.Location), "debug");
                File.WriteAllText(Path.Combine(klasor, "flag_anim.txt"), sb.ToString());
                animKayit = null;
                durum = "Flag animation saved (plugins/debug).";
                Logger.LogInfo("J dump: animation saved");
            }
        }

        private void MeshDok()
        {
            try
            {
                List<Renderer> liste = Hedefler();
                if (liste.Count == 0) { durum = Dil("Flag not found. Equip it first."); return; }

                Mesh mesh = MeshAl(liste[0]);
                if (mesh == null) { durum = Dil("No mesh."); return; }

                Vector3[] v = mesh.vertices;
                Vector2[] uv = mesh.uv;
                int[] t = mesh.triangles;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# mesh=" + mesh.name + " verts=" + v.Length + " tris=" + (t.Length / 3)
                    + " readable=" + mesh.isReadable + " bounds=" + mesh.bounds);

                for (int i = 0; i < v.Length; i++)
                {
                    Vector2 c = i < uv.Length ? uv[i] : Vector2.zero;
                    sb.AppendLine("v " + i + " " + F(v[i].x) + " " + F(v[i].y) + " " + F(v[i].z) + " " + F(c.x) + " " + F(c.y));
                }

                for (int i = 0; i + 2 < t.Length; i += 3)
                    sb.AppendLine("t " + t[i] + " " + t[i + 1] + " " + t[i + 2]);

                string klasor = Path.Combine(Path.GetDirectoryName(Info.Location), "debug");
                Directory.CreateDirectory(klasor);
                string yol = Path.Combine(klasor, "mesh_" + mesh.name + ".txt");
                File.WriteAllText(yol, sb.ToString());
                Logger.LogInfo("Mesh dumped: " + yol);

                // UV adalari (bagli parcalar)
                int[] ebeveyn = new int[v.Length];
                for (int i = 0; i < ebeveyn.Length; i++) ebeveyn[i] = i;

                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    int a = Bul(ebeveyn, t[i]);
                    int b = Bul(ebeveyn, t[i + 1]);
                    int c2 = Bul(ebeveyn, t[i + 2]);
                    ebeveyn[b] = a;
                    ebeveyn[Bul(ebeveyn, c2)] = a;
                }

                Dictionary<int, List<int>> adalar = new Dictionary<int, List<int>>();
                for (int i = 0; i < v.Length; i++)
                {
                    int kok = Bul(ebeveyn, i);
                    List<int> l;
                    if (!adalar.TryGetValue(kok, out l)) { l = new List<int>(); adalar[kok] = l; }
                    l.Add(i);
                }

                foreach (var kv in adalar)
                {
                    float umin = 9f, umax = -9f, vmin = 9f, vmax = -9f;
                    foreach (int i in kv.Value)
                    {
                        if (i >= uv.Length) continue;
                        umin = Mathf.Min(umin, uv[i].x); umax = Mathf.Max(umax, uv[i].x);
                        vmin = Mathf.Min(vmin, uv[i].y); vmax = Mathf.Max(vmax, uv[i].y);
                    }

                    Logger.LogInfo("ISLAND: verts=" + kv.Value.Count
                        + " x_px=[" + F(umin * 256f) + "," + F(umax * 256f) + "]"
                        + " y_png=[" + F((1f - vmax) * 256f) + "," + F((1f - vmin) * 256f) + "]");
                }

                durum = Dil("Mesh dumped: plugins/debug/");
            }
            catch (Exception e)
            {
                Logger.LogError("Mesh dump error: " + e);
                durum = Dil("Mesh dump failed, see log.");
            }
        }
    }
}
