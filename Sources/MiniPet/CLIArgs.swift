import Foundation

// MARK: - CLI Arguments

struct CLIArgs {
    var mobId: String?
    var debugAPI: Bool = false
    var deleteCache: String?
    var update: String?
    var addMob: String?

    static func parse(_ args: [String]) -> CLIArgs {
        var result = CLIArgs()
        var i = 1
        while i < args.count {
            switch args[i] {
            case "--mob":
                if i + 1 < args.count { i += 1; result.mobId = args[i] }
            case "--delete-cache":
                if i + 1 < args.count { i += 1; result.deleteCache = args[i] }
            case "--update":
                if i + 1 < args.count { i += 1; result.update = args[i] }
            case "--add-mob":
                if i + 1 < args.count { i += 1; result.addMob = args[i] }
            case "--debug-api":
                result.debugAPI = true
            case "--help":
                print("用法: MiniPet [选项]")
                print("  --mob <id>              初始怪物ID")
                print("  --delete-cache <codes>   删除缓存（逗号分隔）")
                print("  --update <codes>         强制更新（逗号分隔）")
                print("  --add-mob <codes>        添加怪物到列表（逗号分隔）")
                print("  --debug-api              打印 API 调试信息")
                print("  --help                   显示此帮助")
                exit(0)
            default:
                break
            }
            i += 1
        }
        return result
    }
}

let cli = CLIArgs.parse(CommandLine.arguments)

func runCLIAdminCommands() {
    let scriptPath = "\(projectDir)/fetch_and_generate.py"

    if let codes = cli.deleteCache {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
        task.arguments = [scriptPath, codes, "--delete"]
        task.standardOutput = FileHandle.standardOutput
        task.standardError = FileHandle.standardError
        try? task.run()
        task.waitUntilExit()
        exit(0)
    }

    if let codes = cli.update {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
        task.arguments = [scriptPath, codes, "--update"]
        task.standardOutput = FileHandle.standardOutput
        task.standardError = FileHandle.standardError
        try? task.run()
        task.waitUntilExit()
        exit(0)
    }

    if let codes = cli.addMob {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
        task.arguments = [scriptPath, codes]
        task.standardOutput = FileHandle.standardOutput
        task.standardError = FileHandle.standardError
        try? task.run()
        task.waitUntilExit()
        print("已添加: \(codes)")
        exit(0)
    }
}
