using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Emgu.CV;
using Emgu.Util;
using Emgu.CV.Structure;
using Emgu.CV.OCR;
using System.Net.Http;
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using Emgu.CV.CvEnum;
using System.IO;
using System.Threading;

namespace WarframeRelicRewards {
    public partial class Main : Form {
        private Rectangle[] rectangles;
        private Label[,] labels;
        private String[,] reportStrings = new String[4,2];
        private readonly HttpClient client = new HttpClient();
        private readonly KeyboardHook hook = new KeyboardHook();

        private static Rectangle NormalizedRectangle(int x, int y, int w, int h) {
            return new Rectangle(
                (Screen.PrimaryScreen.Bounds.Width * x) / 1920,
                (Screen.PrimaryScreen.Bounds.Height * y) / 1080,
                (Screen.PrimaryScreen.Bounds.Width * w) / 1920,
                (Screen.PrimaryScreen.Bounds.Height * h) / 1080
            );
        }

        public Main() {
            InitializeComponent();

            submitErrorsCheckBox.Checked = Properties.Settings.Default.submit_errors;
            rectangles = new Rectangle[] {
                NormalizedRectangle(108, 458, 516 - 108, 487 - 458),
                NormalizedRectangle(540, 458, 948 - 540, 487 - 458),
                NormalizedRectangle(972, 458, 1380 - 972, 487 - 458),
                NormalizedRectangle(1404, 458, 1813 - 1404, 487 - 458)
            };
            labels = new Label[,] {
                {name1label, ducats1label, plat1label},
                {name2label, ducats2label, plat2label},
                {name3label, ducats3label, plat3label},
                {name4label, ducats4label, plat4label}
            };
            hook.KeyPressed += HandleHotkey;
            hook.RegisterHotKey(0, Keys.F4);
            System.IO.Directory.CreateDirectory("img");
        }

        private int GetWarframeMarketPrice(string itemName) {
            var item = Items.Normalize(itemName);
            try {
                // normalize item name: NOVA PRIME BLUEPRINT -> nova_prime_blueprint
                var normItem = item.ToLower().Replace(' ', '_');
                var responseString = client.GetStringAsync("https://api.warframe.market/v1/items/" + normItem + "/orders").Result;
                dynamic res = JsonConvert.DeserializeObject(responseString);
                var lowestPrice = int.MaxValue;
                foreach (var order in res.payload.orders) {
                    if (order.platform == "pc" && order.order_type == "sell" && order.user.status != "offline" && order.platinum < lowestPrice) {
                        lowestPrice = order.platinum;
                    }
                }
                return lowestPrice;
            } catch (Exception) {
                return -1;
            }
        }

        private void HandleHotkey(object sender, KeyPressedEventArgs e) {
            // Capture the primary screen
            Bitmap bitmap = new Bitmap(Screen.PrimaryScreen.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
            Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size);
            // Save the image as jpg for possible upload later
            bitmap.Save(@"img\screenshot.jpg", ImageFormat.Jpeg);
            Image<Bgr, byte> image = new Image<Bgr, byte>(bitmap);
            // Apply an adaptive threshold to improve OCR recognition
            image = image
                .Convert<Gray, byte>()
                .ThresholdAdaptive(
                    new Gray(255),
                    AdaptiveThresholdType.GaussianC,
                    Emgu.CV.CvEnum.ThresholdType.Binary,
                    51,
                    new Gray(-10)
                ).Convert<Bgr, byte>();
            bool errors = false;
            var platvalues = new int[4] { -1, -1, -1, -1 };
            var subimages = new Image<Bgr, byte>[4];
            for (int i = 0; i < 4; i++) {
                // Extract the subimage containing the item name
                // this has to be done sequentially and not in parallel
                // TODO: multiline names
                // TODO: other resolutions than Full HD (1920x1080)
                subimages[i] = image.Copy(rectangles[i]);
            }
            // We run both the OCR and the warframe.market query in parallel
            Parallel.For(0, 4, async (i) => {
                var subimage = subimages[i];
                // Save the image as jpg for possible upload later
                subimage.ToBitmap().Save($@"img\{i}.jpg", ImageFormat.Jpeg);
                // Only one OCR instance per thread or runtime errors happen
                var ocr = new Tesseract(
                    @".\tessdata",
                    "weng",
                    OcrEngineMode.TesseractOnly,
                    // Fun fact: no Q or J appears on any name
                    "ABCDEFGHIJKLMNOPRSTUVWXYZ&"
                );
                ocr.SetImage(subimage);
                ocr.Recognize();
                var str = ocr.GetUTF8Text().Trim();
                // Save the originally recognized text
                reportStrings[i, 0] = str;
                var item = EditDistance.BestMatch(str);
                // Save the fixed name
                reportStrings[i, 1] = item;
                if (item == "NOT RECOGNIZED") {
                    errors = true;
                } else {
                    platvalues[i] = GetWarframeMarketPrice(item);
                }
            });
            // Labels can only be edited in the main thread
            for (int i = 0; i < 4; i++) {
                var name = labels[i, 0].Text = reportStrings[i, 1];
                if (platvalues[i] >= 0) {
                    labels[i, 1].Text = Items.Ducats[name] + "";
                    labels[i, 2].Text = platvalues[i] + "";
                } else {
                    labels[i, 1].Text = labels[i, 2].Text = "???";
                }
            }
            WindowState = FormWindowState.Minimized;
            WindowState = FormWindowState.Normal;
            if (errors && Properties.Settings.Default.submit_errors) SubmitErrorAsync();
            else submitErrorButton.Enabled = true;
        }

        private async Task SubmitErrorAsync() {
            var form = new MultipartFormDataContent();
            for (int i = 0; i < 4; i++) {
                form.Add(new StringContent(reportStrings[i, 0]), "item_detected_name_" + i);
                form.Add(new StringContent(reportStrings[i, 1]), "item_computed_name_" + i);
                var img = File.ReadAllBytes($@"img\{i}.jpg");
                form.Add(new ByteArrayContent(img), $"item_img_{i}", $"item_img_{i}");
            }
            var screenshot = File.ReadAllBytes($@"img\screenshot.jpg");
            form.Add(new ByteArrayContent(screenshot), "screenshot", "screenshot");
            HttpResponseMessage response = await client.PostAsync("https://hawkings.tk/wfrelics/upload.php", form);
            //if (!response.IsSuccessStatusCode) {
            //    MessageBox.Show(response.Content.ReadAsStringAsync().Result);
            //}
        }

        private void submitErrorsCheckBox_CheckedChanged(object sender, EventArgs e) {
            Properties.Settings.Default.submit_errors = submitErrorsCheckBox.Checked;
            Properties.Settings.Default.Save();
        }

        private void submitErrorButton_Click(object sender, EventArgs e) {
            SubmitErrorAsync();
            submitErrorButton.Enabled = false;
        }
    }
}
