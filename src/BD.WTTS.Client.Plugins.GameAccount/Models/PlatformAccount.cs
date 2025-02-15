using Avalonia.Platform;
using SkiaSharp;
using AppResources = BD.WTTS.Client.Resources.Strings;

namespace BD.WTTS.Models;

public sealed partial class PlatformAccount
{
    readonly IPlatformSwitcher platformSwitcher;

    public PlatformAccount(ThirdpartyPlatform platform)
    {
        Accounts = new ObservableCollection<IAccount>();
        var platformSwitchers = Ioc.Get<IEnumerable<IPlatformSwitcher>>();

        FullName = platform.ToString();
        Platform = platform;
        platformSwitcher = Platform switch
        {
            ThirdpartyPlatform.Steam => platformSwitchers.OfType<SteamPlatformSwitcher>().First(),
            _ => platformSwitchers.OfType<BasicPlatformSwitcher>().First(),
        };

        SwapToAccountCommand = ReactiveCommand.Create<IAccount>(acc =>
        {
            platformSwitcher.SwapToAccount(acc, this);
            Toast.Show(ToastIcon.Success, AppResources.Success_SwitchAccount__.Format(FullName, acc.DisplayName));
        });

        OpenUrlToBrowserCommand = ReactiveCommand.Create<IAccount>(acc =>
        {
            //Browser2.Open(acc);
        });

        DeleteAccountCommand = ReactiveCommand.Create<IAccount>(async acc =>
        {
            var r = await platformSwitcher.DeleteAccountInfo(acc, this);
            if (r)
                Toast.Show(ToastIcon.Success, Strings.Success_DeletePlatformAccount__.Format(FullName, acc.DisplayName));
        });

        SetAccountAvatarCommand = ReactiveCommand.Create<IAccount>(async acc =>
        {
            await FilePicker2.PickAsync((path) =>
            {
                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(acc.AccountName))
                {
                    var imagePath = Path.Combine(PlatformLoginCache, acc.AccountName, "avatar.png");
                    File.Copy(path, imagePath, true);
                    acc.ImagePath = "";
                    acc.ImagePath = imagePath;
                }
            }, IFilePickerFileType.Images());
        });

        EditRemarkCommand = ReactiveCommand.Create<IAccount>(async acc =>
        {
            var text = await TextBoxWindowViewModel.ShowDialogAsync(new()
            {
                Value = acc.AliasName,
                Title = AppResources.UserChange_EditRemark,
            });
            //可将名字设置为空字符串重置
            if (text == null)
                return;
            acc.AliasName = text;
            platformSwitcher.ChangeUserRemark(acc);
        });

        CopyCommand = ReactiveCommand.Create<string>(async text => await Clipboard2.SetTextAsync(text));

        OpenLinkCommand = ReactiveCommand.Create<string>(async url => await Browser2.OpenAsync(url));

        if (!Directory.Exists(PlatformLoginCache))
            Directory.CreateDirectory(PlatformLoginCache);

        CreateShortcutCommand = ReactiveCommand.Create<IAccount>(acc => CreateShortcut(acc));

        //LoadUsers();
    }

    public void LoadUsers()
    {
        Task2.InBackground(async () =>
        {
            if (IsLoading) return;
            IsLoading = true;
            try
            {
                Accounts.Clear();
                var users = await platformSwitcher.GetUsers(this, () =>
                {
                    if (Accounts.Any_Nullable())
                        foreach (var user in Accounts)
                        {
                            if (user is SteamAccount su)
                            {
                                su.RaisePropertyChanged(nameof(su.ImagePath));
                                su.RaisePropertyChanged(nameof(su.AvatarFramePath));
                            }
                        }
                });

                if (users.Any_Nullable())
                    Accounts = new ObservableCollection<IAccount>(users.OrderByDescending(x => x.LastLoginTime));
            }
            catch (Exception ex)
            {
                ex.LogAndShowT(nameof(PlatformAccount));
            }
            finally
            {
                IsLoading = false;
            }
        });
    }

    public async ValueTask<bool> CurrnetUserAdd(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            await platformSwitcher.NewUserLogin(this);
            return true;
        }
        return await platformSwitcher.CurrnetUserAdd(name, this);
    }

    public async void CreateShortcut(IAccount acc)
    {
#if WINDOWS
        var gear = new[] { 512, 256, 128, 96, 64, 48, 32, 24, 16 };
        using var avatarImgBitmap = await Decode(acc.ImagePath);
        var iconSize = GetImgResolutionPower(Math.Min(avatarImgBitmap.Width, avatarImgBitmap.Height), gear);
        var logoPath = Path.Combine(ProjectUtils.ProjPath, "res", "icons", "app", "v3", "Logo_512.png");
        using var fBitmap = DrawIcon(avatarImgBitmap, SKBitmap.Decode(logoPath), iconSize.Max());
        SKBitmap[]? bitmaps = new[] { fBitmap }.Concat(iconSize.Where(x => x != iconSize.Max())
            .Select(x => fBitmap.Resize(new SKSizeI { Height = x, Width = x }, SKFilterQuality.High)))
            .ToArray();
        var localCachePath = Path.Combine(PlatformLoginCache, acc.AccountName!);
        IOPath.DirCreateByNotExists(localCachePath);
        var clearioc = Directory.GetFiles(localCachePath, "*.ico");
        if (clearioc.Length > 0)
        {
            clearioc.ForEach(x => File.Delete(x));
        }
        var savePath = Path.Combine(localCachePath, $"{Random2.Next()}.ico");
        using var fs = new FileStream(savePath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        IcoEncoder.Encode(fs, bitmaps);
        var deskTopPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) + "\\" + (acc.AccountName ?? acc.AccountId) + ".lnk";
        var processPath = Environment.ProcessPath;
        processPath.ThrowIsNull();
        await platformSwitcher.CreateLoginShortcut(
            deskTopPath,
            processPath,
            $"-clt steam -account {acc.AccountName}",
            null,
            null,
            savePath,
            Path.GetDirectoryName(processPath));
        if (bitmaps != null)
            bitmaps.ForEach(x => x.Dispose());
        Toast.Show(ToastIcon.Success, Strings.CreateShortcutInfo);
#endif
    }

    async Task<SKBitmap> Decode(string? avatarImgPath)
    {
        if (string.IsNullOrWhiteSpace(avatarImgPath))
            return SKBitmap.Decode(AssetLoader.Open(new Uri("avares://BD.WTTS.Client.Avalonia/UI/Assets/avatar.jpg")));
        if (avatarImgPath.StartsWith("https"))
        {
            using var client = new HttpClient();
            using var rspimg = await client.GetStreamAsync(avatarImgPath);
            return SKBitmap.Decode(rspimg);
        }
        return SKBitmap.Decode(avatarImgPath);
    }

    SKBitmap DrawIcon(SKBitmap originalBitmap, SKBitmap loginIcon, int iconSize)
    {
        loginIcon = loginIcon.Resize(new SKSizeI(iconSize / 3, iconSize / 3), SKFilterQuality.High);
        SKBitmap avatarImgBitmap = new(iconSize, iconSize);

        using SKCanvas canvas = new(avatarImgBitmap);
        SKPaint paint = new SKPaint();
        paint.FilterQuality = SKFilterQuality.High;
        canvas.DrawBitmap(originalBitmap.Resize(new SKSizeI(iconSize, iconSize), SKFilterQuality.High),
            new SKRect(0, 0, iconSize, iconSize), paint);

        canvas.DrawBitmap(loginIcon, new SKRect(0, 0, loginIcon.Width, loginIcon.Height));
        return avatarImgBitmap;
    }

    IEnumerable<int> GetImgResolutionPower(int size, int[] rp)
    {
        foreach (var item in rp)
        {
            if (item <= size) yield return item;
        }
    }
}
