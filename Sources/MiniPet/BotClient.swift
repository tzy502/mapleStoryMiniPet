import Foundation

// MARK: - Bot Client (QQ Bot / 长连接)
// 通过 HTTP API 与外部 QQ Bot 通信，让 bot 去调 hermes，
// MiniPet 只负责接收回复并在气泡中显示。

protocol BotClientDelegate: AnyObject {
    func botDidReceiveReply(_ text: String)
    func botDidChangeStatus(_ status: BotStatus)
}

enum BotStatus {
    case idle
    case thinking
    case error(String)
}

class BotClient {
    weak var delegate: BotClientDelegate?
    private var baseURL: String
    private var session = URLSession(configuration: .default)

    init(baseURL: String = "http://127.0.0.1:8080") {
        self.baseURL = baseURL
    }

    /// 向 bot 发送一条消息，bot 会转发给 hermes pet-chat 人格，
    /// 回复通过 delegate.botDidReceiveReply 回调
    func speak(_ text: String) async -> String? {
        delegate?.botDidChangeStatus(.thinking)
        defer { delegate?.botDidChangeStatus(.idle) }

        guard let url = URL(string: "\(baseURL)/api/send") else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let body: [String: Any] = ["message": text, "source": "minipet"]
        req.httpBody = try? JSONSerialization.data(withJSONObject: body)

        do {
            let (data, _) = try await session.data(for: req)
            if let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
               let reply = json["reply"] as? String {
                delegate?.botDidReceiveReply(reply)
                return reply
            }
            return nil
        } catch {
            delegate?.botDidChangeStatus(.error("\(error.localizedDescription)"))
            return nil
        }
    }

    /// 无阻塞触发发言，适合定时闲话
    func triggerRandomChat() {
        Task {
            _ = await speak("说一句短话")
        }
    }
}
