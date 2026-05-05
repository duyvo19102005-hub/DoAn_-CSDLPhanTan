var builder = WebApplication.CreateBuilder(args);
// Thiết lập đường dẫn thư mục App_Data cho LocalDB
string dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
AppDomain.CurrentDomain.SetData("DataDirectory", dataDir);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddSession(options => {
    options.IdleTimeout = TimeSpan.FromMinutes(10); // Session tồn tại trong 10 phút
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();

app.UseSession(); // Kích hoạt Session
// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
