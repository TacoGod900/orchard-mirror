import SwiftUI

struct ContentView: View {
    @State private var name = ""
    @State private var notificationsEnabled = true

    var body: some View {
        NavigationStack {
            VStack(spacing: 18) {
                Image(systemName: "leaf.fill")
                    .foregroundStyle(.green)

                Text("Welcome to Orchard")
                    .font(.largeTitle)

                Text("A clean-room Windows compatibility runtime prototype")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)

                TextField("Your name", text: $name)

                Text("Hello, \(name)")
                    .accessibilityLabel("Live name preview")

                Toggle("Enable preview notifications", isOn: $notificationsEnabled)

                Button("Continue") {
                    print("Continue pressed")
                    name = "Orchard Developer"
                }
                .accessibilityLabel("Continue to the sample application")
            }
            .padding(24)
            .navigationTitle("Hello Orchard")
        }
    }
}
