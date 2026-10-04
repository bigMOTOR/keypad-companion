import Foundation
import Darwin

let expected = "/Applications/Utilities/LogiPluginService.app/Contents/MacOS/LogiPluginService"
func sample(_ pid: Int32) -> [String: Any]? {
    guard pid > 1 else { return nil }
    var path = [CChar](repeating: 0, count: 4096)
    guard proc_pidpath(pid, &path, UInt32(path.count)) > 0,
          String(cString: path) == expected else { return nil }
    var info = proc_taskallinfo()
    let size = Int32(MemoryLayout<proc_taskallinfo>.size)
    guard proc_pidinfo(pid, PROC_PIDTASKALLINFO, 0, &info, size) == size,
          info.pbsd.pbi_pid == UInt32(pid), info.pbsd.pbi_uid == getuid() else { return nil }
    // CPU totals use Mach clock ticks (different on Intel and Apple Silicon).
    var clock = mach_timebase_info_data_t()
    guard mach_timebase_info(&clock) == KERN_SUCCESS, clock.denom > 0 else { return nil }
    // Resident size is bytes. Start time protects PID reuse.
    return ["uid": Int(info.pbsd.pbi_uid), "executable": expected, "pid": Int(pid), "identity": "\(pid):\(info.pbsd.pbi_start_tvsec):\(info.pbsd.pbi_start_tvusec)",
            "cpuSeconds": (Double(info.ptinfo.pti_total_user) + Double(info.ptinfo.pti_total_system)) * Double(clock.numer) / Double(clock.denom) / 1_000_000_000,
            "memoryMiB": Double(info.ptinfo.pti_resident_size) / 1_048_576]
}
func read() -> [String: Any]? {
    if CommandLine.arguments.count == 2, let pid = Int32(CommandLine.arguments[1]), let value = sample(pid) { return value }
    let count = proc_listallpids(nil, 0)
    guard count > 0, count < 100_000 else { return nil }
    var pids = [Int32](repeating: 0, count: Int(count) + 128)
    let actual = pids.withUnsafeMutableBytes { proc_listallpids($0.baseAddress, Int32($0.count)) }
    guard actual >= 0, actual <= pids.count else { return nil }
    for pid in pids.prefix(Int(actual)) { if let value = sample(pid) { return value } }
    return nil
}
let data = try JSONSerialization.data(withJSONObject: read() as Any? ?? NSNull(), options: [.sortedKeys, .fragmentsAllowed])
FileHandle.standardOutput.write(data)
