# kotlinx.serialization keeps its generated serializers; nothing else needs special rules yet.
-keepattributes *Annotation*, InnerClasses
-keepclassmembers class io.github.moontechnologiesco.mooncam.protocol.** { *** Companion; *** serializer(...); }
