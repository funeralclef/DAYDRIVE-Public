/* Receives leaked input beneath the app in the disposable compositor, never on the real desktop. */
#include <gtk/gtk.h>
#include <gtk-layer-shell.h>
#include <stdio.h>
#include <stdlib.h>

static FILE *events;

static gboolean draw(GtkWidget *widget, cairo_t *cr, gpointer data) {
    cairo_set_source_rgb(cr, 64.0 / 255, 64.0 / 255, 64.0 / 255);
    cairo_paint(cr);
    return TRUE;
}

static gboolean button(GtkWidget *widget, GdkEventButton *event, gpointer data) {
    fprintf(events, "button %u %.1f %.1f\n", event->button, event->x, event->y);
    fflush(events);
    return TRUE;
}

static gboolean key(GtkWidget *widget, GdkEventKey *event, gpointer data) {
    fprintf(events, "key %u\n", event->keyval);
    fflush(events);
    return TRUE;
}

int main(int argc, char **argv) {
    if (!getenv("DAYDRIVE_WM_TEST_OUTER_DISPLAY") || !getenv("DAYDRIVE_VERIFY_RUNTIME")) return 2;
    if (argc != 2 || !(events = fopen(argv[1], "w"))) return 2;
    gtk_init(&argc, &argv);
    GtkWidget *window = gtk_window_new(GTK_WINDOW_TOPLEVEL);
    gtk_widget_set_app_paintable(window, TRUE);
    gtk_widget_add_events(window, GDK_BUTTON_PRESS_MASK | GDK_KEY_PRESS_MASK);
    gtk_layer_init_for_window(GTK_WINDOW(window));
    gtk_layer_set_namespace(GTK_WINDOW(window), "daydrive-input-target");
    gtk_layer_set_layer(GTK_WINDOW(window), GTK_LAYER_SHELL_LAYER_BACKGROUND);
    gtk_layer_set_exclusive_zone(GTK_WINDOW(window), -1);
    for (int edge = 0; edge < GTK_LAYER_SHELL_EDGE_ENTRY_NUMBER; edge++)
        gtk_layer_set_anchor(GTK_WINDOW(window), edge, TRUE);
    gtk_layer_set_keyboard_mode(GTK_WINDOW(window), GTK_LAYER_SHELL_KEYBOARD_MODE_ON_DEMAND);
    g_signal_connect(window, "draw", G_CALLBACK(draw), NULL);
    g_signal_connect(window, "button-press-event", G_CALLBACK(button), NULL);
    g_signal_connect(window, "key-press-event", G_CALLBACK(key), NULL);
    g_signal_connect(window, "destroy", G_CALLBACK(gtk_main_quit), NULL);
    gtk_widget_show_all(window);
    gtk_main();
    fclose(events);
    return 0;
}
