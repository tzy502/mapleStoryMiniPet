// 白盒单测可见性：MiniPet.Tests 直测 HermesAdapter internal（MapState / BuildDefaultConnStr / 信号结构）。
// 不用 csproj <InternalsVisibleTo> Item —— RemoveDuplicateAssemblyInfo Target 会把 SDK 生成在 obj/ 的
// AssemblyInfo（含该特性）删掉，故用显式文件（项目根，不在 obj/ 清理范围）。
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MiniPet.Tests")]
[assembly: InternalsVisibleTo("f18probe")]
