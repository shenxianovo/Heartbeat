import OrisumiClient
import SwiftUI

/// The foreground state Observer's dedicated view: today's total, an hourly chart beside a legend of the applications,
/// and the periods. The page scrolls as a whole; only the legend scrolls inside its box, next to the chart.
struct ForegroundToday: View {
    static let packageId = "foreground-state"
    let model: RuntimeModel
    let instanceId: UUID
    @State private var reader = ForegroundReader()
    @State private var icons: AppIcons
    @State private var snapshot: Snapshot<Period>?
    @State private var readError: String?
    /// The application picked in the legend: the chart highlights it and the period list shows only it.
    @State private var selected: String?
    /// The application under the pointer in the legend.
    @State private var hoveredApp: String?

    init(model: RuntimeModel, instanceId: UUID) {
        self.model = model
        self.instanceId = instanceId
        _icons = State(initialValue: AppIcons(model: model))
    }

    var body: some View {
        TimelineView(.everyMinute) { context in
            let now = context.date
            let day = Today.interval(containing: now)
            let periods = snapshot?.items ?? []
            let usage = Today.usage(periods, day: day, now: now)
            let shown = selected.map { key in periods.filter { $0.applicationKey == key } } ?? periods
            VStack(alignment: .leading, spacing: 12) {
                summary(usage: usage)
                Card(padding: 16) {
                    if usage.isEmpty {
                        Text(snapshot == nil ? "正在读取…" : "今天还没有前台时段")
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, minHeight: 120)
                    } else {
                        HStack(alignment: .top, spacing: 20) {
                            TodayChart(periods: periods, usage: usage, day: day, now: now, highlighted: hoveredApp ?? selected)
                            UsageLegend(usage: usage, selected: $selected, hovered: $hoveredApp)
                                .frame(width: 280, height: TodayChart.height)
                        }
                    }
                }
                if !shown.isEmpty {
                    HStack(spacing: 8) {
                        SectionTitle("时段", detail: "\(shown.count) 个")
                        if let selected {
                            Button {
                                self.selected = nil
                            } label: {
                                Label("只看 \(usage.first { $0.id == selected }?.name ?? "未知应用")", systemImage: "xmark.circle.fill")
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                            .help("显示全部应用的时段")
                        }
                    }
                    .padding(.top, 8)
                    Card(padding: 0) {
                        LazyVStack(spacing: 0) {
                            ForEach(shown) { period in
                                PeriodRow(period: period)
                                if period.id != shown.last?.id { Divider().padding(.leading, 12) }
                            }
                        }
                    }
                }
            }
        }
        .environment(icons)
        .task(id: instanceId) {
            snapshot = nil
            while !Task.isCancelled {
                do {
                    snapshot = try await reader.today(instanceId: instanceId, day: Today.interval(containing: .now), from: model)
                    readError = nil
                } catch {
                    readError = error.localizedDescription
                }
                try? await Task.sleep(for: .seconds(2))
            }
        }
    }

    /// Today's total foreground time, and whether what is shown may be missing something.
    private func summary(usage: [Usage]) -> some View {
        let total = usage.reduce(0) { $0 + $1.duration }
        return HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(usage.isEmpty ? "0 分钟" : Format.duration(total))
                .font(.system(size: 30, weight: .semibold, design: .rounded))
                .monospacedDigit()
                .contentTransition(.numericText())
                .help("时长算到最近一次确认")
            Text(usage.isEmpty ? "前台时间" : "前台时间 · \(usage.count) 个应用")
                .foregroundStyle(.secondary)
            Spacer(minLength: 12)
            if let readError {
                Label("无法读取：\(readError)", systemImage: "exclamationmark.triangle.fill")
                    .font(.callout)
                    .foregroundStyle(.red)
                    .lineLimit(1)
            } else if let snapshot, !snapshot.complete {
                Label("后端不可用，只显示本机的数据", systemImage: "icloud.slash")
                    .font(.callout)
                    .foregroundStyle(.orange)
                    .help("API 中已保存的部分暂时读不到，统计可能不完整")
            }
        }
        .padding(.horizontal, 4)
    }
}
