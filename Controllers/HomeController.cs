using System.Data;
using DoAn_CSDLPhanTan.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Polly;
using Polly.CircuitBreaker;

namespace DoAn_CSDLPhanTan.Controllers
{
    public class HomeController : Controller
    {
        private readonly IConfiguration _configuration;

        // KHIÊN BẢO VỆ: Để exceptionsAllowedBeforeBreaking = 1 để đèn đỏ ngay khi có 1 lỗi
        private static readonly AsyncCircuitBreakerPolicy CircuitBreakerMienBac = Policy
            .Handle<SqlException>()
            .CircuitBreakerAsync(exceptionsAllowedBeforeBreaking: 1, durationOfBreak: TimeSpan.FromSeconds(15));

        private static readonly AsyncCircuitBreakerPolicy CircuitBreakerMienNam = Policy
            .Handle<SqlException>()
            .CircuitBreakerAsync(exceptionsAllowedBeforeBreaking: 1, durationOfBreak: TimeSpan.FromSeconds(15));

        public HomeController(IConfiguration configuration) => _configuration = configuration;

        private void GenerateNewCaptcha()
        {
            string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            Random rand = new Random();
            string newCaptcha = new string(Enumerable.Repeat(chars, 6).Select(s => s[rand.Next(s.Length)]).ToArray());
            HttpContext.Session.SetString("CaptchaCode", newCaptcha);
            ViewBag.CaptchaDisplay = newCaptcha;
        }

        // Đã xóa bớt các thẻ [HttpGet] dư thừa
        [HttpGet]
        public IActionResult Index()
        {
            // Dùng thống nhất CircuitState.Closed do đã khai báo using Polly.CircuitBreaker;
            ViewBag.IsNodeBacOnline = CircuitBreakerMienBac.CircuitState == CircuitState.Closed;
            ViewBag.IsNodeNamOnline = CircuitBreakerMienNam.CircuitState == CircuitState.Closed;

            GenerateNewCaptcha();
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Index(string soBaoDanh, string captchaInput)
        {
            string connBac = @"Server=(localdb)\mssqllocaldb;Database=DB_MienBac;Trusted_Connection=True;Connect Timeout=1;";
            string connNam = @"Server=(localdb)\mssqllocaldb;Database=DB_MienNam;Trusted_Connection=True;Connect Timeout=1;";

            ViewBag.IsNodeBacOnline = SimpleCheck(connBac);
            ViewBag.IsNodeNamOnline = SimpleCheck(connNam);
            // 1. Kiểm tra Captcha
            string sessionCaptcha = HttpContext.Session.GetString("CaptchaCode") ?? "";
            GenerateNewCaptcha(); // Luôn tạo mới mã sau mỗi lần bấm

            if (string.IsNullOrEmpty(captchaInput) || !captchaInput.Trim().Equals(sessionCaptcha, StringComparison.OrdinalIgnoreCase))
            {
                ViewBag.Error = "Mã xác nhận không chính xác!";
                UpdateViewStatus();
                return View();
            }

            // 2. Xác định trạm tra cứu (Bổ sung chặn SBD rỗng)
            if (string.IsNullOrEmpty(soBaoDanh) || !int.TryParse(soBaoDanh, out int sbdInt))
            {
                ViewBag.Error = "SBD phải là số hợp lệ!";
                UpdateViewStatus();
                return View();
            }

            string connString = "";
            AsyncCircuitBreakerPolicy activeBreaker;
            bool isBacNode = (sbdInt >= 1 && sbdInt <= 500);

            if (isBacNode)
            {
                connString = _configuration.GetConnectionString("NodeMienBac") ?? "";
                activeBreaker = CircuitBreakerMienBac;
                ViewBag.Node = "Trạm Miền Bắc";
            }
            else
            {
                connString = _configuration.GetConnectionString("NodeMienNam") ?? "";
                activeBreaker = CircuitBreakerMienNam;
                ViewBag.Node = "Trạm Miền Nam";
            }

            // 3. Chặn lỗi chuỗi kết nối Null
            if (string.IsNullOrEmpty(connString))
            {
                ViewBag.Error = $"Cấu hình kết nối {ViewBag.Node} không tồn tại!";
                UpdateViewStatus();
                return View();
            }

            try
            {
                ThiSinh? ts = null;
                await activeBreaker.ExecuteAsync(async () =>
                {
                    using (SqlConnection conn = new SqlConnection(connString))
                    {
                        await conn.OpenAsync();
                        string query = "SELECT * FROM ThiSinh WHERE MaTS = @MaTS";
                        using (SqlCommand cmd = new SqlCommand(query, conn))
                        {
                            cmd.Parameters.AddWithValue("@MaTS", soBaoDanh.PadLeft(3, '0'));
                            using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                            {
                                if (await reader.ReadAsync())
                                {
                                    ts = new ThiSinh
                                    {
                                        MaTS = reader["MaTS"].ToString(),
                                        HoTen = reader["HoTen"].ToString(),
                                        DiemToan = Convert.ToDouble(reader["DiemToan"]),
                                        DiemVan = Convert.ToDouble(reader["DiemVan"]),
                                        DiemAnh = Convert.ToDouble(reader["DiemAnh"])
                                    };
                                }
                            }
                        }
                    }
                });

                UpdateViewStatus(); // Mạch thành công -> Xanh
                if (ts != null) return View(ts);

                ViewBag.Error = "Không tìm thấy dữ liệu!";
                return View();
            }
            catch (Exception ex)
            {
                if (ex is BrokenCircuitException)
                    ViewBag.Maintenance = $"{ViewBag.Node} đang tạm dừng bảo vệ hệ thống. Thử lại sau 15 giây.";
                else
                    ViewBag.Maintenance = $"{ViewBag.Node} hiện đang tạm ngưng bảo trì. Rất xin lỗi!";

                System.Diagnostics.Debug.WriteLine($"[Lỗi Hệ Thống] {ViewBag.Node}: {ex.Message}");
                UpdateViewStatus(); // Mạch lỗi -> Đỏ
                return View();
            }
        }

        private void UpdateViewStatus()
        {
            // Dùng thống nhất CircuitState.Closed
            ViewBag.IsNodeBacOnline = CircuitBreakerMienBac.CircuitState == CircuitState.Closed;
            ViewBag.IsNodeNamOnline = CircuitBreakerMienNam.CircuitState == CircuitState.Closed;
        }

        [HttpGet]
        public IActionResult GetNodesStatus()
        {
            // LẤY CHÍNH XÁC CHUỖI KẾT NỐI MÀ BẠN DÙNG TRONG HÀM TRA CỨU
            string connBac = @"Server=(localdb)\mssqllocaldb;Database=DB_MienBac;Trusted_Connection=True;Connect Timeout=2;";
            string connNam = @"Server=(localdb)\mssqllocaldb;Database=DB_MienNam;Trusted_Connection=True;Connect Timeout=2;";

            return Json(new
            {
                nodeBac = SimpleCheck(connBac),
                nodeNam = SimpleCheck(connNam)
            });
        }

        private bool SimpleCheck(string connectionString)
        {
            try
            {
                using (var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString))
                {
                    connection.Open();
                    // Nếu mở được và trạng thái là Open thì chắc chắn sống
                    bool isOpen = (connection.State == System.Data.ConnectionState.Open);
                    connection.Close();
                    return isOpen;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}