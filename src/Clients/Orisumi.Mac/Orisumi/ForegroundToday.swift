import OrisumiClient
import SwiftUI

/// The foreground state Observer's dedicated view: today's applications as an hourly chart, a legend and the
/// periods. The header stays in place; the legend and the period list scroll inside their own boxes.
struct ForegroundToday: View {
    static let packageId = "foreground-state"
    let model: RuntimeModel
    let instanceId: UUID
    @State private var reader = ForegroundReader()
    @State private var snapshot: Snapshot<Period>?
    @State private var readError: String?
    /// The application picked in the legend: the chart highlights it and the period list shows only it.
    @State private var selected: String?
    /// The application under the pointer in the legend.
    @State private var hoveredApp: String?

    var body: some View {
        TimelineView(.everyMinute) { context in
            let now = context.date
            let day = Today.interval(containing: now)
            let periods = snapshot?.items ?? []
            let usage = Today.usage(periods, day: day, now: now)
            let shown = selected.map { key in periods.filter { $0.applicationKey == key } } ?? periods
            VStack(alignment: .leading, spacing: 8) {
                SectionTitle("今天", detail: detail(usage: usage))
                Card {
                    if usage.isEmpty {
                        Text(snapshot == nil ? "正在读取…" : "今天还没有前台时段")
                            .foregroundStyle(.secondary)
                    } else {
                        HStack(alignment: .top, spacing: 20) {
                            TodayChart(periods: periods, usage: usage, day: day, now: now, highlighted: hoveredApp)
                            UsageLegend(usage: usage, selected: $selected, hovered: $hoveredApp)
                                .frame(width: 300, height: TodayChart.height)
                        }
                    }
                }
                .padding(.bottom, 8)

                HStack(spacing: 8) {
                    SectionTitle("时段", detail: shown.isEmpty ? nil : "\(shown.count) 个")
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
                Card(padding: 0) {
                    ScrollView {
                        LazyVStack(spacing: 0) {
                            ForEach(shown) { period in
                                PeriodRow(period: period)
                                if period.id != shown.last?.id { Divider().padding(.leading, 40) }
                            }
                        }
                    }
                    .defaultScrollAnchor(.top)
                    .scrollIndicators(.visible)
                    .frame(minHeight: PeriodRow.height * 4, maxHeight: .infinity)
                }
                Text("时长算到最近一次确认。其他日期的记录在 Web 控制台中查看。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 4)
            }
        }
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

    private func detail(usage: [Usage]) -> String? {
        if let readError { return "无法读取：\(readError)" }
        let total = usage.reduce(0) { $0 + $1.duration }
        let summary = usage.isEmpty ? nil : "\(usage.count) 个应用，共 \(Format.duration(total))"
        if let snapshot, !snapshot.complete {
            return [summary, "后端不可用，只显示本机的数据，可能不完整"].compactMap(\.self).joined(separator: " · ")
        }
        return summary
    }
}
