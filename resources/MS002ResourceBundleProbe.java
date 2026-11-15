import java.util.Locale;
import java.util.ResourceBundle;

/**
 * Installed Signal runtime diagnostic. Probes the same Java ResourceBundle API
 * that argparse4j invokes when signal-cli starts. This helper is compiled by
 * the verified Windows CI build, then SHA-256 signed in the runtime manifest.
 * It never reads Signal accounts, credentials, messages, or network data.
 */
public final class MS002ResourceBundleProbe {
    public static void main(String[] args) {
        try {
            String base = "net.sourceforge.argparse4j.internal.ArgumentParserImpl";
            ResourceBundle bundle = ResourceBundle.getBundle(base, Locale.US);
            if (bundle.keySet().isEmpty()) {
                System.err.println("MS002_BUNDLE_EMPTY");
                System.exit(3);
            }
            System.out.println("MS002_BUNDLE_OK");
        } catch (Exception e) {
            System.err.println("MS002_BUNDLE_ERROR:" + e.getClass().getSimpleName());
            System.exit(2);
        }
    }
}
