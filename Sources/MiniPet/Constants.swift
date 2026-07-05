import Foundation

// MARK: - Constants

let apiBases = ["http://127.0.0.1:10502", "http://192.168.3.46:10502"]
let cacheRoot = (NSHomeDirectory() as NSString).appendingPathComponent("Library/Caches/MiniPet")
let sessionPath = (NSHomeDirectory() as NSString).appendingPathComponent(".pet/pet_session.jsonl")
let projectDir = "/Users/a502/IdeaProjects/mapleStoryMiniPet"

let fallbackMobs: [(code: String, name: String)] = [
    ("9602078", "黑暗奥尔卡"),
    ("9602448", "光冕塞伦"),
]
