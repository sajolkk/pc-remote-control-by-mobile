# ML Kit (the QR scanner behind mobile_scanner) finds its components by instantiating each
# ComponentRegistrar reflectively from a manifest entry. The libraries' own rules keep the classes
# but not their constructors, and since AGP 9 R8 no longer keeps a default constructor implicitly.
# Without this the registrars cannot be created, the barcode component is never registered, and
# the camera fails with a NullPointerException as soon as the scanner starts.
-keep class * implements com.google.firebase.components.ComponentRegistrar {
    <init>();
}
