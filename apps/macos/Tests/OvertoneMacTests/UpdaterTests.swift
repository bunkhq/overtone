import Foundation
import Testing
@testable import OvertoneMac

@Suite struct UpdaterTests {
    @Test func versionOrdering() {
        #expect(AppVersion.compare("0.2.0", "0.1.0") > 0)
        #expect(AppVersion.compare("v0.10.0", "0.9.9") > 0)
        #expect(AppVersion.compare("0.2.0", "0.2.0") == 0)
        #expect(AppVersion.compare("0.2", "0.2.0") == 0)
        #expect(AppVersion.compare("0.2.0", "0.2.0-dev.5") > 0)
        #expect(AppVersion.compare("0.2.0-dev.10", "0.2.0-dev.9") > 0)
        #expect(AppVersion.compare("0.1.0", "0.2.0-dev.1") < 0)
    }

    @Test func checksumLines() {
        let sums = """
        0a1b2c  Overtone-0.2.0-macOS.dmg
        ffee99 *Overtone-0.2.0-Windows-x64-Setup.exe
        """
        #expect(Updater.matches(sums, name: "Overtone-0.2.0-macOS.dmg", hash: "0A1B2C"))
        #expect(Updater.matches(sums, name: "Overtone-0.2.0-Windows-x64-Setup.exe", hash: "ffee99"))
        #expect(!Updater.matches(sums, name: "Overtone-0.2.0-macOS.dmg", hash: "ffee99"))
        #expect(!Updater.matches(sums, name: "other.dmg", hash: "0a1b2c"))
    }

    @Test func releasePayload() throws {
        let json = #"""
        {"tag_name":"v0.2.0","html_url":"https://github.com/bunkhq/overtone/releases/tag/v0.2.0","assets":[
          {"name":"Overtone-0.2.0-Windows-x64-Setup.exe","browser_download_url":"https://example.com/a.exe"},
          {"name":"Overtone-0.2.0-macOS.dmg","browser_download_url":"https://example.com/b.dmg"},
          {"name":"SHA256SUMS","browser_download_url":"https://example.com/SHA256SUMS"}]}
        """#
        let r = try #require(try Updater.parse(Data(json.utf8)))
        #expect(r.version == "0.2.0")
        #expect(r.dmgName == "Overtone-0.2.0-macOS.dmg")
        #expect(r.dmg?.absoluteString == "https://example.com/b.dmg")
        #expect(r.sums?.absoluteString == "https://example.com/SHA256SUMS")
    }
}
