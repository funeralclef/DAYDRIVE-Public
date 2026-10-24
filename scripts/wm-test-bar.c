/* An exclusive-zone bar for the isolated compositor's placement checks. */
#include <gtk/gtk.h>
#include <gtk-layer-shell.h>

int main(int argc, char **argv) {
    gtk_init(&argc, &argv);
    GtkWidget *widget = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    GtkWindow *window = GTK_WINDOW(widget);
    gtk_layer_init_for_window(window);
    gtk_layer_set_namespace(window, "daydrive-test-bar");
    gtk_layer_set_layer(window, GTK_LAYER_SHELL_LAYER_TOP);
    gtk_layer_set_anchor(window, GTK_LAYER_SHELL_EDGE_TOP, TRUE);
    gtk_layer_set_anchor(window, GTK_LAYER_SHELL_EDGE_LEFT, TRUE);
    gtk_layer_set_anchor(window, GTK_LAYER_SHELL_EDGE_RIGHT, TRUE);
    gtk_layer_set_exclusive_zone(window, 32);
    gtk_layer_set_keyboard_mode(window, GTK_LAYER_SHELL_KEYBOARD_MODE_NONE);
    gtk_widget_set_size_request(widget, -1, 32);
    gtk_widget_show(widget);
    gtk_main();
    return 0;
}
