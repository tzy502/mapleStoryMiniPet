import Foundation

// MARK: - Logging

func logDebug(_ msg: String) {
    if cli.debugAPI { FileHandle.standardError.write(Data(("[MiniPet] " + msg + "\n").utf8)) }
}

// MARK: - ANSI Escape Code Stripper

func stripANSI(_ s: String) -> String {
    var result = ""
    result.reserveCapacity(s.utf8.count)
    var i = s.startIndex
    while i < s.endIndex {
        let c = s[i]
        if c == "\u{1B}" {
            i = s.index(after: i)
            guard i < s.endIndex else { break }
            let next = s[i]
            if next == "[" {
                i = s.index(after: i)
                while i < s.endIndex {
                    let ch = s[i]; i = s.index(after: i)
                    if (ch >= "a" && ch <= "z") || (ch >= "A" && ch <= "Z") || ch == "~" { break }
                }
            } else if next == "]" {
                i = s.index(after: i)
                while i < s.endIndex {
                    let ch = s[i]; i = s.index(after: i)
                    if ch == "\u{07}" || ch == "\u{1B}" { break }
                }
            } else {
                i = s.index(after: i)
            }
        } else if c == "\r" {
            i = s.index(after: i)
        } else {
            result.append(c)
            i = s.index(after: i)
        }
    }
    return result
}
