import Charts
import OrisumiClient
import SwiftUI

/// Foreground minutes in each of today's 24 hours, stacked by application.
/// Hovering an hour shades its column and shows its breakdown above the bars.
struct TodayChart: View {
    static let height: CGFloat = 220
    let periods: [Period]
    let usage: [Usage]
    let day: DateInterval
    let now: Date
    /// The application to stand out; the others fade.
    let highlighted: String?
    @State private var hovered: Date?

    var body: some View {
        let keys = AppVisuals.colored(usage)
        let slices = Today.hourly(periods, range: day, now: now, keys: keys)
        Chart {
            if let hovered {
                RectangleMark(x: .value("小时", hovered, unit: .hour), yStart: .value("分钟", 0), yEnd: .value("分钟", 60),
                              width: .ratio(0.8))
                    .foregroundStyle(.primary.opacity(0.06))
            }
            ForEach(slices) { slice in
                BarMark(x: .value("小时", slice.hour, unit: .hour), y: .value("分钟", slice.duration / 60), width: .ratio(0.7))
                    .foregroundStyle(AppVisuals.color(for: slice.key, among: keys)
                        .opacity(highlighted == nil || highlighted == slice.key ? 1 : 0.22))
            }
        }
        .chartXScale(domain: day.start...day.end)
        .chartYScale(domain: 0...60)
        .chartXAxis {
            AxisMarks(values: .stride(by: .hour, count: 3)) { value in
                AxisValueLabel(anchor: .top) {
                    if let date = value.as(Date.self) { Text(Format.time(date)) }
                }
            }
        }
        .chartYAxis {
            AxisMarks(position: .leading, values: [0, 20, 40, 60]) { value in
                AxisGridLine(stroke: StrokeStyle(lineWidth: 0.5, dash: [3]))
                AxisValueLabel { if let minutes = value.as(Int.self) { Text(minutes == 0 ? "0" : "\(minutes)分") } }
            }
        }
        .chartOverlay { proxy in
            GeometryReader { geometry in
                let plot = proxy.plotFrame.map { geometry[$0] } ?? .zero
                ZStack(alignment: .topLeading) {
                    Rectangle().fill(.clear).contentShape(Rectangle())
                        .onContinuousHover { phase in
                            guard case .active(let location) = phase,
                                  let date: Date = proxy.value(atX: location.x - plot.minX),
                                  let hour = Calendar.current.dateInterval(of: .hour, for: date)?.start,
                                  day.contains(hour) else {
                                hovered = nil
                                return
                            }
                            hovered = hour
                        }
                    // Drawn above the chart, so it covers the bars.
                    if let hovered, let x = proxy.position(forX: hovered.addingTimeInterval(1_800)),
                       slices.contains(where: { $0.hour == hovered }) {
                        let column = plot.minX + x
                        let width = HourTooltip.width
                        let gap = plot.width / 24 * 0.5 + 8
                        let left = column + gap + width <= geometry.size.width ? column + gap : column - gap - width
                        HourTooltip(periods: periods, hour: DateInterval(start: hovered, duration: 3_600), now: now, keys: keys)
                            .offset(x: max(0, left), y: plot.minY + 4)
                            .allowsHitTesting(false)
                    }
                }
            }
        }
        .frame(height: Self.height)
    }
}

/// The applications of one hour, longest first, with the hour's total.
struct HourTooltip: View {
    static let width: CGFloat = 250
    let periods: [Period]
    let hour: DateInterval
    let now: Date
    let keys: [String]

    var body: some View {
        let usage = Today.usage(periods, day: hour, now: now)
        let shown = usage.prefix(8)
        let rest = usage.dropFirst(8)
        VStack(alignment: .leading, spacing: 6) {
            Text(hour.start.formatted(Date.VerbatimFormatStyle(
                format: "\(year: .defaultDigits)/\(month: .defaultDigits)/\(day: .defaultDigits) \(hour: .twoDigits(clock: .twentyFourHour, hourCycle: .zeroBased)):00",
                timeZone: .current, calendar: .current)))
                .font(.headline)
            ForEach(shown) { item in
                HStack(spacing: 8) {
                    RoundedRectangle(cornerRadius: 2)
                        .fill(AppVisuals.color(for: keys.contains(item.id) ? item.id : nil, among: keys))
                        .frame(width: 8, height: 8)
                    Text(item.name ?? "未知应用").lineLimit(1)
                    Spacer(minLength: 12)
                    Text(Format.duration(item.duration)).monospacedDigit()
                }
            }
            if !rest.isEmpty {
                Text("另外 \(rest.count) 个应用，\(Format.duration(rest.reduce(0) { $0 + $1.duration }))")
                    .foregroundStyle(.secondary)
            }
            Divider()
            HStack {
                Text("合计")
                Spacer(minLength: 12)
                Text("\(Format.duration(usage.reduce(0) { $0 + $1.duration })) · \(usage.reduce(0) { $0 + $1.periods }) 个时段")
                    .monospacedDigit()
            }
            .foregroundStyle(.secondary)
        }
        .font(.callout)
        .padding(12)
        .frame(width: Self.width, alignment: .leading)
        .background(.background, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.separator, lineWidth: 0.5))
        .shadow(color: .black.opacity(0.12), radius: 12, y: 4)
    }
}

/// Today's applications, longest first. Hovering one highlights it in the chart; clicking one filters the periods.
struct UsageLegend: View {
    let usage: [Usage]
    @Binding var selected: String?
    @Binding var hovered: String?

    var body: some View {
        let keys = AppVisuals.colored(usage)
        let total = usage.reduce(0) { $0 + $1.duration }
        ScrollView {
            LazyVStack(spacing: 2) {
                ForEach(usage) { item in
                    LegendRow(usage: item, color: AppVisuals.color(for: keys.contains(item.id) ? item.id : nil, among: keys),
                              longest: usage.first?.duration ?? 1, total: total,
                              hovered: hovered == item.id, filtering: selected == item.id)
                        .contentShape(Rectangle())
                        .onTapGesture { selected = selected == item.id ? nil : item.id }
                        .onHover { inside in
                            if inside { hovered = item.id } else if hovered == item.id { hovered = nil }
                        }
                        .help(item.name ?? "未知应用")
                }
            }
        }
        .defaultScrollAnchor(.top)
        .scrollIndicators(.visible)
    }
}

struct LegendRow: View {
    static let height: CGFloat = 64
    let usage: Usage
    let color: Color
    let longest: TimeInterval
    let total: TimeInterval
    let hovered: Bool
    let filtering: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 8) {
                RoundedRectangle(cornerRadius: 2).fill(color).frame(width: 8, height: 8)
                AppIcon(identifiers: usage.identifiers, size: 18)
                Text(usage.name ?? "未知应用").lineLimit(1)
                if filtering {
                    Image(systemName: "line.3.horizontal.decrease.circle.fill")
                        .foregroundStyle(.secondary)
                        .help("时段列表只显示这个应用")
                }
                Spacer(minLength: 8)
                Text(Format.duration(usage.duration)).fontWeight(.semibold).monospacedDigit()
            }
            GeometryReader { geometry in
                ZStack(alignment: .leading) {
                    Capsule().fill(.quaternary)
                    Capsule().fill(color).frame(width: max(4, geometry.size.width * usage.duration / max(longest, 1)))
                }
            }
            .frame(height: 4)
            Text("\(usage.periods) 个时段 · 最长 \(Format.duration(usage.longest)) · \(Int((usage.duration / max(total, 1) * 100).rounded()))%")
                .font(.caption)
                .foregroundStyle(.secondary)
                .monospacedDigit()
        }
        .padding(.horizontal, 8)
        .frame(height: Self.height)
        .background(hovered ? AnyShapeStyle(.quaternary) : AnyShapeStyle(.clear), in: RoundedRectangle(cornerRadius: 8))
    }
}
