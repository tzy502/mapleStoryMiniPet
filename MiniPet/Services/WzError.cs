namespace MiniPet.Services
{
    /// <summary>
    /// WZ 加载错误类型
    /// </summary>
    public enum WzError
    {
        /// <summary>
        /// DLL 文件不存在
        /// </summary>
        DllNotFound,

        /// <summary>
        /// DLL 加载失败
        /// </summary>
        DllLoadFailed,

        /// <summary>
        /// BaseWZ 路径无效
        /// </summary>
        WzPathInvalid,

        /// <summary>
        /// WZ 文件损坏
        /// </summary>
        WzFileCorrupted,

        /// <summary>
        /// 版本不兼容
        /// </summary>
        VersionMismatch,

        /// <summary>
        /// 未知错误
        /// </summary>
        Unknown
    }
}
